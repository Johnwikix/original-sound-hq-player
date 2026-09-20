using WinUIMusicPlayer.Model;
using Windows.Storage;

// Platform adapters only: the linked scanner, SQLite transactions, VM and commands are production code.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace Microsoft.UI { public readonly record struct WindowId(ulong Value); }
namespace WinUIMusicPlayer.Model { public static class AppData { public static IntPtr HWnd => new(1); } }
namespace WinRT.Interop
{
    public static class InitializeWithWindow
    {
        public static void Initialize(object picker, IntPtr owner)
        {
            if (owner == IntPtr.Zero) throw new InvalidOperationException("Missing owner");
        }
    }
}
namespace Windows.Storage.Pickers
{
    public class FolderPicker
    {
        public List<string> FileTypeFilter { get; } = [];
        public static Func<Task<StorageFolder?>> Pick = () => Task.FromResult<StorageFolder?>(null);
        public Task<StorageFolder?> PickSingleFolderAsync()
        {
            if (!FileTypeFilter.Contains("*")) throw new InvalidOperationException("Missing filter");
            return Pick();
        }
    }
}
namespace CommunityToolkit.WinUI
{
    public static class DispatcherExtensions
    {
        public static Task EnqueueAsync(this TestDispatcher dispatcher, Func<Task> action) => action();
        public static Task EnqueueAsync(this TestDispatcher dispatcher, Action action)
        {
            action();
            return Task.CompletedTask;
        }
    }
    public class TestDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
namespace Microsoft.Windows.Storage.Pickers
{
    public class PickFolderResult { public string Path { get; init; } = ""; }
    public class FolderPicker(Microsoft.UI.WindowId id)
    {
        public static Func<Task<PickFolderResult?>> Pick = () => Task.FromResult<PickFolderResult?>(null);
        public Task<PickFolderResult?> PickSingleFolderAsync() => Pick();
    }
}
namespace Windows.UI.ViewManagement { public enum ViewSizePreference { UseMore } }
namespace Windows.System
{
    public class FolderLauncherOptions { public UI.ViewManagement.ViewSizePreference DesiredRemainingView { get; set; } }
    public static class Launcher
    {
        public static Task<bool> LaunchFolderPathAsync(string path) => Task.FromResult(true);
        public static Task LaunchFolderAsync(StorageFolder folder, FolderLauncherOptions options) => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer
{
    public class TestWindow
    {
        public CommunityToolkit.WinUI.TestDispatcher DispatcherQueue { get; } = new();
        public TestWindow AppWindow => this;
        public Microsoft.UI.WindowId Id => new(1);
    }
}
namespace WinUIMusicPlayer.View { public class MainPage { public object XamlRoot { get; } = new(); } }
namespace WinUIMusicPlayer.Helper
{
    public static class DialogHelper
    {
        public static Func<Task<bool>> Confirm = () => Task.FromResult(true);
        public static int Calls;
        public static Task<bool> ShowConfirmAsync(object root, string key) { Calls++; return Confirm(); }
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public class AppViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged { add { } remove { } }
        public bool IsPlaybackEngineReady => false;
        public List<Music> SongsSource { get; } = [];
        public Music? FindById(int id) => SongsSource.Find(music => music.Id == id);
        public int SourceNotifications { get; private set; }
        public void NotifySongsSourceChanged() => SourceNotifications++;
        public event Action<IReadOnlyList<Music>>? BatchApplied;
        public void AppendSongsBatch(IReadOnlyList<Music> batch)
        {
            SongsSource.AddRange(batch);
            BatchApplied?.Invoke(batch);
        }
        public async Task RefreshSongsSourceAsync()
        {
            var database = (Services.MusicDatabaseService)App.Services.GetService(typeof(Services.MusicDatabaseService))!;
            var songs = await database.GetMusicListAsync();
            SongsSource.Clear();
            SongsSource.AddRange(songs);
        }
    }
}
namespace WinUIMusicPlayer.Services
{
    public static class AutoRescanService
    {
        public static List<SubFolder> RecordInitialFolderTimes(string root, int id) =>
            [new SubFolder { Path = root, FolderId = id }];
    }
    public partial class MusicDatabaseService
    {
        private readonly Microsoft.Extensions.Logging.ILogger<MusicDatabaseService> _logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<MusicDatabaseService>.Instance;
        public SQLite.SQLiteAsyncConnection Connection => _dbConnection;
        private readonly SQLite.SQLiteAsyncConnection _dbConnection;
        private readonly AddFolderService addFolderService = new();
        private readonly SemaphoreSlim _rescanfolderSemaphore = new(4, 4);
        public Task<Music?> FindMusicByPathAsync(string path) => _dbConnection.FindWithQueryAsync<Music>(
            "SELECT * FROM Music WHERE Path = ? COLLATE NOCASE LIMIT 1", path)!;
        public MusicDatabaseService(string path) => _dbConnection = new(path);
        public async Task InitializeAsync()
        {
            await _dbConnection.CreateTableAsync<Music>();
            await _dbConnection.CreateTableAsync<PendingMetadataWrite>();
            await _dbConnection.CreateTableAsync<Folder>();
            await _dbConnection.CreateTableAsync<SubFolder>();
            await _dbConnection.CreateTableAsync<MusicLyrics>();
            await _dbConnection.ExecuteAsync("CREATE INDEX IX_Music_Path_NoCase ON Music(Path COLLATE NOCASE)");
        }
        public Task<List<Folder>> GetFolders() => _dbConnection.Table<Folder>().ToListAsync();
        public Task<List<Music>> GetMusicListAsync() => _dbConnection.Table<Music>().ToListAsync();
        public Task InsertSubFolders(List<SubFolder> folders) => _dbConnection.InsertAllAsync(folders);
    }
}

namespace WinUIMusicPlayer.Services
{
    public sealed class NotificationService
    {
        public static int Count;
        public void SendNotification(string title, string message) => Count++;
    }
}
