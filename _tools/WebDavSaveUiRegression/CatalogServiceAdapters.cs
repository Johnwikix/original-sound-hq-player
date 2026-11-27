using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services
{
    public partial class MusicDatabaseService(SQLiteAsyncConnection connection)
    {
        private readonly SQLiteAsyncConnection _dbConnection = connection;
        public Task WhenInitialized => Task.CompletedTask;
        public SongVersionStore Versions { get; } = new(connection);
        public LyricsRepository Lyrics { get; } = new(connection, new LyricsParser());
    }
}

namespace WinUIMusicPlayer.ViewModel
{
    // The actual WinUI dispatcher hosts the published songs. The shell's other pages are outside this fixture.
    public sealed class AppViewModel(MusicDatabaseService database, DispatcherQueue dispatcher)
    {
        public event Action? SongsSourceChanged;
        public event PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public TestState State { get; } = new();
        public List<Music> SongsSource { get; } = [];
        public List<Music> CurrentPlayingList { get; } = [];
        public Music? CurrentPlayingMusic => null;
        public bool PublishedOffUiThread { get; private set; }

        public Music? FindById(int id) => SongsSource.Find(music => music.Id == id);

        public void AppendSongsBatch(List<Music> songs)
        {
            PublishedOffUiThread |= !dispatcher.HasThreadAccess;
            SongsSource.AddRange(songs);
            SongsSourceChanged?.Invoke();
        }

        public async Task RefreshSongsSourceAsync(CancellationToken token = default)
        {
            var (music, _) = await database.Versions.ReadLibraryAsync(false).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            var refresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                SongsSource.Clear();
                SongsSource.AddRange(music);
                SongsSourceChanged?.Invoke();
                refresh.TrySetResult();
            })) throw new InvalidOperationException("Fixture dispatcher rejected the library refresh.");
            await refresh.Task.ConfigureAwait(false);
        }
    }

    public sealed class TestState
    {
        public TestPreferences Preferences { get; } = new();
    }
    public sealed class TestPreferences : ObservableObject { }
}

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings { public static string MusicCoverCache { get; set; } = ""; }
    public static class AppData { public static string SystemLanguage => "en"; }
}

namespace WinUIMusicPlayer.Services.WebDav
{
    public sealed record RemoteMetadata(string Title, string Artist, string Album, int Track, int Disc, int Year,
        int SampleRate, int Channels, int BitDepth, int BitRate, double DurationMs,
        AdditionalMusicMetadata? AdditionalMetadata = null, string CodecId = "", int EncodingKind = 0);

    public static class RemoteMetadataProbe
    {
        public static RemoteMetadata ReadMetadata(Stream input, string extension, CancellationToken token) =>
            throw new InvalidOperationException("The WebDAV fixture disables native metadata reads.");
    }
}

namespace WinUIMusicPlayer.Reader
{
    public static class AudioCoverReader
    {
        public static byte[] ReadCover(Stream input, string extension) =>
            throw new InvalidOperationException("Only authenticated catalog cover endpoints are tested.");
    }
}

namespace WinUIMusicPlayer
{
    public static class App { public static Window MainWindow { get; set; } = null!; }
}

namespace WinUIMusicPlayer.Helper
{
    internal static class PlaybackCoverImage
    {
        public const string CacheSuffix = "_display.bin";
        public static string GetCachePath(string path) => path + CacheSuffix;
    }
}

namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils { public static string GetString(string key) => key; }
}
