using WinUIMusicPlayer.Model;

// These adapters keep activation/playback unavailable: tests exercise the real asynchronous
// resolver and SQLite writes, then explicitly verify the production folder publication method.
namespace Microsoft.Windows.AppLifecycle
{
    public enum ExtendedActivationKind { File }
    public sealed class AppInstance
    {
        public static AppInstance GetCurrent() => new();
        public ActivationArgs GetActivatedEventArgs() => new();
    }
    public sealed class ActivationArgs
    {
        public ExtendedActivationKind Kind { get; init; }
        public object? Data { get; init; }
    }
}
namespace Windows.ApplicationModel.Activation
{
    public interface IFileActivatedEventArgs
    {
        IReadOnlyList<Windows.Storage.StorageFile> Files { get; }
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class AppLifecycle
    {
        public bool IsReady => false;
        public event EventHandler? Changed { add { } remove { } }
    }
    public partial class MusicDatabaseService
    {
        public Task WhenInitialized => Task.CompletedTask;
        public async Task<(string?, string?, string?, string?)> GetLyricsAsync(int id)
        {
            var lyrics = await _dbConnection.FindAsync<MusicLyrics>(id);
            return (lyrics?.Lyrics, lyrics?.TranslatedLyrics, lyrics?.Krc, lyrics?.TKrc);
        }
        public Task SaveLyricsAsync(int id, string? lrc, string? trans, string? krc, string? tkrc) =>
            _dbConnection.InsertOrReplaceAsync(new MusicLyrics
            {
                MusicId = id, Lyrics = lrc ?? "", TranslatedLyrics = trans ?? "", Krc = krc ?? "", TKrc = tkrc ?? ""
            });
    }
    public sealed record CachedLyrics(string? Lrc, string? Trans, string? Krc, string? TKrc);
    public static class OneShotLyricsCache
    {
        public static CachedLyrics? Load(string path) => null;
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class MusicBrowseViewModel
    {
        public void PlayMusicWithFolderQueue(Music music) => throw new InvalidOperationException("Playback is not ready in resolver tests");
        public Task PlayMusic(Music music) => throw new InvalidOperationException("Playback is not ready in resolver tests");
    }
}
