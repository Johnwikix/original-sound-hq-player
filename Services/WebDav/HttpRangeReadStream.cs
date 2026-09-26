using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>供同步标签/封面解析器使用的有限网络流；只能在有界后台工作任务中调用。</summary>
public sealed class HttpRangeReadStream : Stream
{
    private const int BlockSize = 128 * 1024;
    private readonly WebDavTransport _transport;
    private readonly WebDavConnection _source;
    private readonly WebDavEntry _entry;
    private readonly CancellationTokenSource _deadline;
    private readonly Dictionary<long, (byte[] Buffer, int Count)> _blocks = new();
    private readonly long _budget;
    private readonly int _maxRequests;
    private long _readBytes, _position;
    private int _requests;
    private bool _disposed;
    private readonly RemoteResourceVersion _version;
    private Exception? _failure;
    public void ThrowIfFailed()
    {
        _deadline.Token.ThrowIfCancellationRequested();
        if (_failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(_failure).Throw();
    }

    public HttpRangeReadStream(WebDavTransport transport, WebDavConnection source, WebDavEntry entry,
        CancellationToken token, long budget = 2 * 1024 * 1024, int maxRequests = 16, int timeoutSeconds = 15)
    {
        if (entry.Length < 0) throw new WebDavException("UnknownLength");
        _transport = transport;
        _source = source;
        _entry = entry;
        _version = new(entry);
        _budget = budget;
        _maxRequests = maxRequests;
        _deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        _deadline.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    }

    public long DownloadedBytes => _readBytes;
    public override bool CanRead => !_disposed;
    public override bool CanSeek => !_disposed;
    public override bool CanWrite => false;
    public override long Length => _entry.Length;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _deadline.Token.ThrowIfCancellationRequested();
        int total = 0;
        while (!buffer.IsEmpty && _position < Length)
        {
            long start = _position / BlockSize * BlockSize;
            if (!_blocks.TryGetValue(start, out var block))
            {
                try { block = FetchAsync(start).GetAwaiter().GetResult(); }
                catch (Exception ex) { _failure = ex; throw; }
                _blocks.Add(start, block);
            }
            int offset = (int)(_position - start);
            int count = Math.Min(buffer.Length, block.Count - offset);
            if (count <= 0) throw new EndOfStreamException();
            block.Buffer.AsSpan(offset, count).CopyTo(buffer);
            _position += count;
            total += count;
            buffer = buffer[count..];
        }
        return total;
    }

    private async Task<(byte[], int)> FetchAsync(long start)
    {
        int count = (int)Math.Min(BlockSize, Length - start);
        if (_requests >= _maxRequests || _readBytes + count > _budget) throw new WebDavException("ReadBudgetExceeded");
        _requests++;
        await using var response = await _transport.OpenAsync(_source, _entry.Href, start, start + count - 1, false, _deadline.Token).ConfigureAwait(false);
        var range = response.Message.Content.Headers.ContentRange;
        if (response.Message.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.To != start + count - 1 || range.Length is null)
            throw new WebDavException("RangeNotSupported");
        if (range.Length != Length)
            throw new WebDavException("ResourceChanged");
        _version.Validate(response);
        byte[] bytes = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            await response.Stream.ReadExactlyAsync(bytes.AsMemory(0, count), _deadline.Token).ConfigureAwait(false);
            _readBytes += count;
            return (bytes, count);
        }
        catch { ArrayPool<byte>.Shared.Return(bytes); throw; }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        long target = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
        if (target < 0) throw new IOException("Negative position.");
        return _position = target;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            foreach (var block in _blocks.Values) ArrayPool<byte>.Shared.Return(block.Buffer);
            _blocks.Clear();
            _deadline.Dispose();
        }
        base.Dispose(disposing);
    }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
