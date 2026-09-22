using WinUIMusicPlayer.Services.WebDav;

internal static class ConnectionProbe
{
    public static async Task RunAsync(WebDavTransport transport)
    {
        var root = WebDavTransport.NormalizeRoot(Environment.GetEnvironmentVariable("MUSIC_WEBDAV_URL") ?? throw new InvalidOperationException("Set MUSIC_WEBDAV_URL."));
        string? fingerprint = Environment.GetEnvironmentVariable("MUSIC_WEBDAV_CERTIFICATE_SHA256");
        var connection = new WebDavConnection(root,
            Environment.GetEnvironmentVariable("MUSIC_WEBDAV_USER") ?? "",
            Environment.GetEnvironmentVariable("MUSIC_WEBDAV_PASSWORD") ?? "",
            string.IsNullOrEmpty(fingerprint) ? null : new(root.GetLeftPart(UriPartial.Authority), fingerprint));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            int entries = 0;
            await foreach (var _ in transport.ListAsync(connection, connection.Root.AbsolutePath, timeout.Token)) entries++;
            Console.WriteLine($"PROPFIND OK: {entries} entries.");
        }
        catch (WebDavException ex)
        {
            Console.WriteLine($"PROPFIND failed: {ex.Code}, HTTP {(int?)ex.Status}.");
            if (ex is WebDavCertificateException certificate)
                Console.WriteLine($"Certificate SHA-256: {certificate.Certificate.Sha256}; name mismatch: {certificate.Certificate.NameMismatch}; valid: {certificate.Certificate.CanTrust}.");
            Environment.ExitCode = 1;
        }
        catch (HttpRequestException ex)
        {
            Console.WriteLine($"PROPFIND failed: {ex.HttpRequestError}, cause {ex.InnerException?.GetType().Name}.");
            Environment.ExitCode = 1;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("PROPFIND timed out.");
            Environment.ExitCode = 1;
        }
    }
}
