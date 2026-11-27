using System.Collections.ObjectModel;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

// Only the shell/library publication boundary, playback commands and raw artwork I/O are substituted.
// The page, statistics queries, observable rows, view model, behavior, WIC decode and WinRT source are production code.
namespace WinUIMusicPlayer.Model
{
    public sealed class Music
    {
        [PrimaryKey] public int Id { get; set; }
        public int SongId { get; set; }
        public string Title { get; set; } = "Song";
        public string Author { get; set; } = "Artist";
        public string Album { get; set; } = "Album";
        public string Path { get; set; } = "";
        public long CoverAudioWriteTicks { get; set; }
        public string CoverSourceStamp { get; set; } = "";
        public string CoverOverridePath { get; set; } = "";
        public string LocalTrackKey => Path;
        public string ImageHash { get; set; } = "";
        public bool IsRemote { get; set; }
        public int PlayCount { get; set; }
        public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(3);
    }
    public sealed class BulkObservableCollection<T>(IEnumerable<T> source) : ObservableCollection<T>(source);
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel
    {
        public List<Music> SongsSource { get; } = [];
        public BulkObservableCollection<Music> SequentialPlayingList { get; set; } = new([]);
        public event Action? SongsSourceChanged;
        public event Action<long>? CurrentPlayingTimeChanged;
        public bool CanPublishState => true;
        public bool IsPlaying => false;
        public bool TryFindById(int id, out Music? music)
        {
            music = SongsSource.Find(item => item.Id == id);
            return music is not null;
        }
        public void Publish(params Music[] songs)
        {
            SongsSource.Clear();
            SongsSource.AddRange(songs);
            SongsSourceChanged?.Invoke();
        }
    }
}
namespace WinUIMusicPlayer.ViewModel.Pages
{
    public sealed class MusicBrowseViewModel
    {
        public Task PlayMusic(Music music, bool IsChangeList) => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService(SQLiteAsyncConnection connection)
    {
        public Task WhenInitialized => Task.CompletedTask;
        public SQLiteAsyncConnection GetDbConnection() => connection;
    }

    public sealed class WebDavLibraryService
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<int, (byte[] Bytes, bool CanMatch)> Covers = new();

        public async Task<(byte[] Bytes, bool CanMatch)> ReadCoverResultAsync(Music music, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Covers.TryGetValue(music.Id, out var result)) return ([], false);
            string path = MatchedPath(music);
            return result.CanMatch && File.Exists(path)
                ? (await File.ReadAllBytesAsync(path, token), false)
                : result;
        }

        public Task CacheMatchedCoverAsync(Music music, string imageHash, byte[] picture, CancellationToken token)
        {
            string path = MatchedPath(music);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            return File.WriteAllBytesAsync(path, picture, token);
        }

        private static string MatchedPath(Music music)
            => WebDavCachePaths.MatchedCover(Utils.AppSettings.MusicCoverCache, music.ImageHash.Length > 0 ? music.ImageHash : $"legacy-{music.Id}");
    }
}
namespace WinUIMusicPlayer.WebService
{
    public sealed class LrcService
    {
        public int MatchCount;
        public Func<Music, CancellationToken, Task<byte[]?>> MatchAsync { get; set; } = (_, _) => Task.FromResult<byte[]?>(null);

        public Task<byte[]?> GetMixedCoverImageAsync(Music music, CancellationToken token)
        {
            Interlocked.Increment(ref MatchCount);
            return MatchAsync(music, token);
        }
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class AppSettings
    {
        public static string MusicCoverCache { get; set; } = "";
        public static bool IsAutoCoverEnabled { get; set; }
    }
    public static class CjkStringComparer
    {
        public static int Compare(string? left, string? right) => string.CompareOrdinal(left, right);
    }
    public partial class ToolUtils
    {
        public static string GetString(string key) => new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader().GetString(key);
        internal static string FindRawCachePath(string hash) => WebDavCachePaths.FindCover(AppSettings.MusicCoverCache, hash);
        public static int Reads;
        public static TaskCompletionSource? ReadGate;
        public static Action<Music>? ReadStarted;
        public static async Task<byte[]> GetRawImage(Music music, bool manual, CancellationToken token)
        {
            if (music.IsRemote && music.Path.StartsWith("remote://", StringComparison.Ordinal))
                return await GetRemoteRawImageAsync(music, manual, token);
            Interlocked.Increment(ref Reads);
            ReadStarted?.Invoke(music);
            if (ReadGate is { } gate) await gate.Task.WaitAsync(token);
            return await File.ReadAllBytesAsync(music.Path, token);
        }
    }
}
