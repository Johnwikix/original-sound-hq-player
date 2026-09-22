using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

// A seekable 1 GiB DSF with ID3 at EOF; audio is generated per read, never allocated as a file.
internal sealed class VirtualDsf : Stream
{
    internal static readonly byte[] Picture = [0xff, 0xd8, 1, 2, 3, 0xff, 0xd9];
    private const long AudioBytes = 1024L * 1024 * 1024;
    private readonly byte[] _header = new byte[92];
    private readonly byte[] _tag;
    public long BytesRead;
    internal VirtualDsf(int dsdRate = 2822400)
    {
        byte[] apic = [0, .. Encoding.ASCII.GetBytes("image/jpeg"), 0, 3, 0, .. Picture];
        _tag = new byte[20 + apic.Length];
        "ID3"u8.CopyTo(_tag); _tag[3] = 3; _tag[9] = (byte)(10 + apic.Length);
        "APIC"u8.CopyTo(_tag.AsSpan(10)); BinaryPrimitives.WriteInt32BigEndian(_tag.AsSpan(14), apic.Length);
        apic.CopyTo(_tag, 20);
        "DSD "u8.CopyTo(_header);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(4), 28);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(12), Length);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(20), 92 + AudioBytes);
        "fmt "u8.CopyTo(_header.AsSpan(28)); BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(32), 52);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(40), 1);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(48), 2);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(52), 2);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(56), dsdRate);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(60), 1);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(64), AudioBytes * 4);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(72), 4096);
        "data"u8.CopyTo(_header.AsSpan(80)); BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(84), AudioBytes + 12);
    }
    public override int Read(Span<byte> destination)
    {
        int count = (int)Math.Min(destination.Length, Length - Position);
        var buffer = destination[..count]; buffer.Fill(0x69);
        if (Position < 92) _header.AsSpan((int)Position, (int)Math.Min(count, 92 - Position)).CopyTo(buffer);
        long tagStart = 92 + AudioBytes;
        if (Position + count > tagStart)
        {
            long start = Math.Max(Position, tagStart);
            _tag.AsSpan((int)(start - tagStart), (int)(Position + count - start)).CopyTo(buffer[(int)(start - Position)..]);
        }
        Position += count; BytesRead += count; return count;
    }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => 92 + AudioBytes + _tag.Length;
    public override long Position { get; set; }
    public override long Seek(long offset, SeekOrigin origin) => Position = offset + (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal sealed class DsfHttpFixture : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly System.Collections.Concurrent.ConcurrentBag<Task> _requests = [];
    private readonly long _budget;
    public long BytesSent;
    public volatile bool Stall;
    public readonly ManualResetEventSlim Blocked = new();
    private readonly int _dsdRate;
    internal string Url { get; }
    internal DsfHttpFixture(long budget = long.MaxValue, int dsdRate = 2822400)
    {
        _budget = budget; _dsdRate = dsdRate; _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/audio.dsf";
        _loop = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try { while (!_stop.IsCancellationRequested) { var client = await _listener.AcceptTcpClientAsync(_stop.Token); _requests.Add(ServeAsync(client)); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string? first = await reader.ReadLineAsync(_stop.Token);
            if (first == null) return;
            long start = 0, end;
            using var dsf = new VirtualDsf(_dsdRate); end = dsf.Length - 1;
            bool range = false;
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
                if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line[13..].Split('-'); start = long.Parse(parts[0]);
                    if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1])); range = true;
                }
            string header = $"HTTP/1.1 {(range ? "206 Partial Content" : "200 OK")}\r\nAccept-Ranges: bytes\r\nContent-Length: {end - start + 1}\r\nConnection: close\r\n" + (range ? $"Content-Range: bytes {start}-{end}/{dsf.Length}\r\n" : "") + "\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token);
            if (Stall) { Blocked.Set(); await Task.Delay(Timeout.Infinite, _stop.Token); }
            if (first.StartsWith("HEAD ")) return;
            dsf.Position = start; byte[] buffer = new byte[32768];
            while (dsf.Position <= end && Interlocked.Read(ref BytesSent) < _budget)
            {
                int count = dsf.Read(buffer, 0, (int)Math.Min(buffer.Length, end - dsf.Position + 1));
                await stream.WriteAsync(buffer.AsMemory(0, count), _stop.Token);
                Interlocked.Add(ref BytesSent, count);
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
    }
    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop(); _loop.GetAwaiter().GetResult();
        Task.WhenAll(_requests).GetAwaiter().GetResult(); Blocked.Dispose(); _stop.Dispose();
    }
}
