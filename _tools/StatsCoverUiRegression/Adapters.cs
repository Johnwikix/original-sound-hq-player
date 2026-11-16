using System.Collections.ObjectModel;
using SQLite;
using WinUIMusicPlayer.Model;

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
        public SQLiteAsyncConnection GetDbConnection() => connection;
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class AppSettings
    {
        public static string MusicCoverCache { get; set; } = "";
    }
    public static class CjkStringComparer
    {
        public static int Compare(string? left, string? right) => string.CompareOrdinal(left, right);
    }
    public static class ToolUtils
    {
        public static int Reads;
        public static TaskCompletionSource? ReadGate;
        public static async Task<byte[]> GetRawImage(Music music, bool manual, CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            if (ReadGate is { } gate) await gate.Task.WaitAsync(token);
            return await File.ReadAllBytesAsync(music.Path, token);
        }
    }
}
