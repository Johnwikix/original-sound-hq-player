using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

// SQLite persistence and both ViewModels are production code. Device playback, vault, cache and main app are adapters.
namespace WinUIMusicPlayer.Services
{
    public partial class MusicDatabaseService(SQLiteAsyncConnection connection)
    {
        private readonly SQLiteAsyncConnection _dbConnection = connection;
        public Task WhenInitialized => Task.CompletedTask;
    }
    public sealed record WebDavScanStatus(int SourceId, string Phase, int Found, int Tagged, string? Error = null);
    public sealed class WebDavLibraryService(MusicDatabaseService? database = null)
    {
        public CancellationToken StoppingToken => CancellationToken.None;
        public WebDavSource? Saved { get; private set; }
        public event Action? SourcesChanged;
        public event Action<WebDavScanStatus>? StatusChanged { add { } remove { } }
        public WebDavScanStatus? GetStatus(int sourceId) => null;
        public WebDavConnection Connect(WebDavSource source) => new(WebDavTransport.NormalizeRoot(source.BaseUri), source.UserName, "",
            string.IsNullOrEmpty(source.TrustedCertificateSha256) ? null : new(source.TrustedCertificateOrigin, source.TrustedCertificateSha256));
        public Task CancelScanAsync(int id) => Task.CompletedTask;
        public async Task SaveSourceAsync(WebDavSource source, string password)
        {
            await Task.Yield();
            if (database is not null) await database.SaveWebDavSourceAsync(source);
            Saved = source;
            SourcesChanged?.Invoke();
        }
        public async Task RemoveSourceAsync(WebDavSource source)
        {
            await database!.RemoveWebDavSourceAsync(source.Id);
            SourcesChanged?.Invoke();
        }
        public Task ScanAsync(WebDavSource source) => Task.CompletedTask;
        public Task ApplyCacheSettingsAsync(WebDavCacheSettings settings) => Task.CompletedTask;
    }
    public sealed class RemotePlaybackService { public Task StopAsync() => Task.CompletedTask; }
    public sealed class LibraryQueries
    {
        public int Filter { get; private set; } = -1;
        public void SetSourceFilter(int id) => Filter = id;
    }
    public static class MusicCommands
    {
        public static void OnLyricsOffsetChanged(Music music, int value) { }
        public static ICommand PlayCommand => null!;
        public static ICommand UpdateFavouriteCommand => null!;
        public static ICommand AddToPlayListCommand => null!;
    }
}
namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed class RemoteAudioCache { public long GetSize() => 0; public void Clear() { } }
    public sealed record RemoteMetadata(string Title, string Artist, string Album, int Track, int Disc, int Year,
        int SampleRate, int Channels, int BitDepth, int BitRate, double DurationMs);
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel
    {
        public TestState State { get; } = new();
        public string MusicCoverCache => Path.GetTempPath();
        public Music? CurrentPlayingMusic => null;
        public int Refreshes { get; private set; }
        public void RefreshDataSource() => Refreshes++;
    }
    public sealed class TestState
    {
        public TestBrowse Browse { get; } = new();
        public TestPreferences Preferences { get; } = new();
    }
    public sealed class TestBrowse { public int SourceFilterId { get; set; } = -1; }
    public sealed class TestPreferences : ObservableObject { }
}
namespace WinUIMusicPlayer.Model { public static class AppSettings { public static string MusicCoverCache => ""; } }
namespace WinUIMusicPlayer { public static class App { public static Window MainWindow { get; set; } = null!; } }
namespace WinUIMusicPlayer.Helper
{
    public static class DialogHelper { public static Task<bool> ShowConfirmAsync(XamlRoot root, string key) => Task.FromResult(true); }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key == "WebDavCertificateDetails" ? "{0}\n{1}\n{2}\n{3:g} — {4:g}\n{5}\n{6}" : key;
    }
}
namespace AnimatedWin2dControls.Messages { public sealed class Unused { } }
