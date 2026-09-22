using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

internal sealed class Fixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Task _accept;
    public readonly byte[] Bytes = new byte[3 * 1024 * 1024 + 137];
    public string Root { get; }
    public int Gets, UnauthorizedRedirects;
    public Fixture()
    {
        new Random(42).NextBytes(Bytes);
        _listener.Start();
        Root = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/dav/";
        _accept = AcceptAsync();
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _requests.Add(HandleAsync(client));
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
            string first = await reader.ReadLineAsync(_stop.Token) ?? "";
            string? range = null, auth = null;
            while (await reader.ReadLineAsync(_stop.Token) is { Length: > 0 } line)
            {
                if (line.StartsWith("Range:")) range = line[6..].Trim();
                if (line.StartsWith("Authorization:")) auth = line[14..].Trim();
            }
            var parts = first.Split(' ');
            string path = parts[1];
            if (parts[0] == "PROPFIND")
            {
                string xml = "<d:multistatus xmlns:d=\"DAV:\">" +
                    "<d:response><d:href>/dav/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>" +
                    "<d:response><d:href>/dav/%E9%9F%B3%E4%B9%90</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>" +
                    $"<d:response><d:href>/dav/tone.flac</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>{Bytes.Length}</d:getcontentlength><d:getetag>&quot;v1&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response></d:multistatus>";
                byte[] body = Encoding.UTF8.GetBytes(xml);
                await Header(207, body.Length, "");
                await stream.WriteAsync(body, _stop.Token);
                return;
            }
            Interlocked.Increment(ref Gets);
            if (path.Contains("redirect")) { await Header(302, 0, $"Location: {Root.Replace("127.0.0.1", "localhost").Replace("/dav/", "/cdn/")}file\r\n"); return; }
            if (path.StartsWith("/cdn/") && auth is not null) Interlocked.Increment(ref UnauthorizedRedirects);
            if (path.Contains("unauthorized")) { await Header(401, 0, ""); return; }
            bool partial = range is not null && !path.Contains("no-range");
            int start = 0, end = Bytes.Length - 1;
            if (partial)
            {
                var numbers = range![6..].Split('-');
                start = int.Parse(numbers[0]);
                if (numbers[1].Length > 0) end = int.Parse(numbers[1]);
            }
            string etag = path.Contains("changed") ? "\"v2\"" : "\"v1\"";
            await Header(partial ? 206 : 200, end - start + 1,
                $"ETag: {etag}\r\n" + (partial ? $"Content-Range: bytes {start}-{end}/{Bytes.Length}\r\n" : ""));
            if (path.Contains("stall")) await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            await stream.WriteAsync(Bytes.AsMemory(start, end - start + 1), _stop.Token);
            async Task Header(int status, int length, string extra) =>
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Length: {length}\r\nConnection: close\r\n{extra}\r\n"), _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_requests);
        _stop.Dispose();
    }
}
