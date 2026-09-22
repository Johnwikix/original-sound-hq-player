using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>只服务一个资源的 loopback 端点。使用 TCP 避免安装 HTTP URL ACL，关闭连接结束每个响应。</summary>
public sealed class WebDavPlaybackBridge : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<Task> _requests = [];
    private readonly object _gate = new();
    private readonly RemoteReadSession _session;
    private readonly string _path = "/" + Guid.NewGuid().ToString("N") + "/audio";
    private Task _accept = Task.CompletedTask;
    private bool _disposed;
    public string Location { get; private set; } = "";
    public string? Error { get; private set; }
    public WebDavPlaybackBridge(RemoteReadSession session) => _session = session;
    public void Start()
    {
        _listener.Start(4);
        Location = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}{_path}";
        _accept = AcceptAsync();
    }
    public void SetPlaying(bool playing) => _session.SetPlaying(playing);
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_requests.Count >= 4) { client.Dispose(); continue; }
                    var task = Task.Run(() => HandleAsync(client));
                    _requests.Add(task);
                    _ = ObserveAsync(task);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }
    private async Task ObserveAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        finally { lock (_gate) _requests.Remove(task); }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        using (var registration = _stop.Token.Register(static value => ((TcpClient)value!).Dispose(), client))
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                var stream = client.GetStream();
                using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                headersTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                int length = 0;
                while (length < 8192)
                {
                    int count = await stream.ReadAsync(buffer.AsMemory(length, 8192 - length), headersTimeout.Token).ConfigureAwait(false);
                    if (count == 0) return;
                    length += count;
                    if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
                }
                if (length == 8192 && buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) < 0) return;
                var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n", StringSplitOptions.None);
                var first = lines[0].Split(' ');
                if (first.Length != 3 || first[1] != _path || first[0] is not ("GET" or "HEAD"))
                { await HeaderAsync(stream, 404, 0, null); return; }
                long total = _session.Entry.Length, start = 0, end = total - 1;
                bool partial = false;
                foreach (string line in lines)
                {
                    if (!line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) continue;
                    if (partial || !RangeHeaderValue.TryParse(line[6..].Trim(), out var range) || range.Unit != "bytes" || range.Ranges.Count != 1 || total <= 0)
                    { await HeaderAsync(stream, 416, 0, $"bytes */{total}"); return; }
                    foreach (var item in range.Ranges)
                    {
                        start = item.From ?? Math.Max(0, total - (item.To ?? 0));
                        end = item.From is null ? total - 1 : Math.Min(total - 1, item.To ?? total - 1);
                    }
                    partial = true;
                    if (start > end || start < 0 || start >= total)
                    { await HeaderAsync(stream, 416, 0, $"bytes */{total}"); return; }
                }
                if (first[0] == "HEAD") { await HeaderAsync(stream, partial ? 206 : 200, total < 0 ? null : end - start + 1, partial ? $"bytes {start}-{end}/{total}" : null); return; }
                if (total < 0)
                {
                    await HeaderAsync(stream, 200, null, null);
                    await _session.CopySequentialAsync(stream, _stop.Token).ConfigureAwait(false);
                    return;
                }
                int read;
                try { read = await _session.ReadAsync(buffer.AsMemory(0, (int)Math.Min(65536, end - start + 1)), start, _stop.Token).ConfigureAwait(false); }
                catch (WebDavException ex) when (ex.Code == "RangeNotSupported" && start == 0)
                {
                    await HeaderAsync(stream, 200, total, null, false);
                    await _session.CopySequentialAsync(stream, _stop.Token).ConfigureAwait(false);
                    return;
                }
                await HeaderAsync(stream, partial ? 206 : 200, end - start + 1, partial ? $"bytes {start}-{end}/{total}" : null);
                long position = start;
                while (read > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, read), _stop.Token).ConfigureAwait(false);
                    position += read;
                    if (position > end) break;
                    read = await _session.ReadAsync(buffer.AsMemory(0, (int)Math.Min(65536, end - position + 1)), position, _stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or System.Net.Http.HttpRequestException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                if (!_stop.IsCancellationRequested && ex is WebDavException dav) Error = dav.Code;
            }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
    private async Task HeaderAsync(Stream stream, int status, long? length, string? range, bool seekable = true)
    {
        string header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : status == 206 ? "Partial Content" : status == 416 ? "Range Not Satisfiable" : "Not Found")}\r\nConnection: close\r\nContent-Type: application/octet-stream\r\n";
        if (length is not null) header += $"Content-Length: {length}\r\n";
        if (range is not null) header += "Content-Range: " + range + "\r\n";
        if (seekable) header += "Accept-Ranges: bytes\r\n";
        header += "\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), _stop.Token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        _listener.Stop();
        await _accept.ConfigureAwait(false);
        Task[] requests;
        lock (_gate) { requests = new Task[_requests.Count]; _requests.CopyTo(requests); }
        await Task.WhenAll(requests).ConfigureAwait(false);
        await _session.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
