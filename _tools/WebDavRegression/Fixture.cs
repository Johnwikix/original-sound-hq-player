using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

internal sealed class Fixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _requests = [];
    private readonly Task _accept;
    private readonly X509Certificate2? _certificate;
    public readonly byte[] Bytes = new byte[3 * 1024 * 1024 + 137];
    public string Root { get; }
    public int Gets, UnauthorizedRedirects;
    public string CdnETag = "\"cdn-v1\"";
    public bool CdnHasETag = true, CdnSupportsRange = true;
    public DateTimeOffset CdnModified = new(2026, 9, 25, 15, 6, 11, TimeSpan.Zero);
    public int CdnLengthDelta;
    public Fixture(bool tls = false, bool nameMismatch = false, bool expired = false)
    {
        if (tls)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder();
            if (nameMismatch) san.AddDnsName("nas.invalid");
            else san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(expired ? -1 : 1));
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
                X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
        }
        new Random(42).NextBytes(Bytes);
        _listener.Start();
        Root = $"{(tls ? "https" : "http")}://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/dav/";
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
            using Stream stream = _certificate is null ? client.GetStream() : new SslStream(client.GetStream(), false);
            if (stream is SslStream ssl)
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.Tls12 }, _stop.Token);
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
                const string musicFolder = "/dav/%E9%9F%B3%E4%B9%90/";
                string requested = path.StartsWith(musicFolder + "live/", StringComparison.Ordinal) ? musicFolder + "live/"
                    : path.StartsWith(musicFolder + "studio/", StringComparison.Ordinal) ? musicFolder + "studio/"
                    : path.StartsWith(musicFolder, StringComparison.Ordinal) ? musicFolder : "/dav/";
                string xml = "<d:multistatus xmlns:d=\"DAV:\">" +
                    $"<d:response><d:href>{requested}</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>" +
                    (requested == "/dav/"
                        ? "<d:response><d:href>/dav/%E9%9F%B3%E4%B9%90</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>" +
                          $"<d:response><d:href>/dav/tone.flac</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>{Bytes.Length}</d:getcontentlength><d:getetag>&quot;v1&quot;</d:getetag></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                        : requested == musicFolder
                            ? "<d:response><d:href>/dav/%E9%9F%B3%E4%B9%90/live/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>" +
                              "<d:response><d:href>/dav/%E9%9F%B3%E4%B9%90/studio/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>"
                            : "") +
                    "</d:multistatus>";
                byte[] body = Encoding.UTF8.GetBytes(xml);
                await Header(207, body.Length, "");
                await stream.WriteAsync(body, _stop.Token);
                return;
            }
            Interlocked.Increment(ref Gets);
            if (path.Contains("redirect")) { await Header(302, 0, $"Location: {Root.Replace("127.0.0.1", "localhost").Replace("/dav/", "/cdn/")}file\r\n"); return; }
            if (path.StartsWith("/cdn/") && auth is not null) Interlocked.Increment(ref UnauthorizedRedirects);
            if (path.Contains("unauthorized")) { await Header(401, 0, ""); return; }
            bool cdn = path.StartsWith("/cdn/");
            bool partial = range is not null && !path.Contains("no-range") && (!cdn || CdnSupportsRange);
            int start = 0, end = Bytes.Length - 1;
            if (partial)
            {
                var numbers = range![6..].Split('-');
                start = int.Parse(numbers[0]);
                if (numbers[1].Length > 0) end = int.Parse(numbers[1]);
            }
            string etag = cdn ? CdnETag : path.Contains("changed") ? "\"v2\"" : "\"v1\"";
            await Header(partial ? 206 : 200, end - start + 1,
                (cdn && !CdnHasETag ? "" : $"ETag: {etag}\r\n") +
                (cdn ? $"Last-Modified: {CdnModified:R}\r\n" : "") +
                (partial ? $"Content-Range: bytes {start}-{end}/{Bytes.Length + (cdn ? CdnLengthDelta : 0)}\r\n" : ""));
            if (path.Contains("stall")) await Task.Delay(TimeSpan.FromSeconds(30), _stop.Token);
            await stream.WriteAsync(Bytes.AsMemory(start, end - start + 1), _stop.Token);
            async Task Header(int status, int length, string extra) =>
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Response\r\nContent-Length: {length}\r\nConnection: close\r\n{extra}\r\n"), _stop.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or AuthenticationException) { }
    }
    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accept;
        await Task.WhenAll(_requests);
        _certificate?.Dispose();
        _stop.Dispose();
    }
}
