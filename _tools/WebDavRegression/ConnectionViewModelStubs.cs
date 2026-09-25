using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

// Only the application's database/vault boundary is substituted. TLS, transport and the ViewModel are production code.
namespace WinUIMusicPlayer.Services
{
    public sealed class WebDavLibraryService
    {
        public CancellationToken StoppingToken => CancellationToken.None;
        public WebDavSource? Saved { get; private set; }
        public WebDavConnection Connect(WebDavSource source) => new(WebDavTransport.NormalizeRoot(source.BaseUri), source.UserName, "",
            string.IsNullOrEmpty(source.TrustedCertificateSha256) ? null : new(source.TrustedCertificateOrigin, source.TrustedCertificateSha256));
        public Task CancelScanAsync(int id) => Task.CompletedTask;
        public async Task SaveSourceAsync(WebDavSource source, string password) { await Task.Yield(); Saved = source; }
        public Task ScanAsync(WebDavSource source) => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key == "WebDavCertificateDetails" ? "{0}\n{1}\n{2}\n{3:g} — {4:g}\n{5}\n{6}" : key;
    }
}
