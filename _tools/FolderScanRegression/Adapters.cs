using WinUIMusicPlayer.Model;
using Windows.Storage;

// Platform adapters only: the linked scanner, SQLite transactions, VM and commands are production code.
namespace Microsoft.UI.Xaml
{
    public enum Visibility { Visible, Collapsed }
}
namespace CommunityToolkit.WinUI
{
    public static class DispatcherExtensions
    {
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
    public class FolderPicker(int id)
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
        public static Task LaunchFolderAsync(StorageFolder folder, FolderLauncherOptions options) => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer
{
    public class TestWindow
    {
        public CommunityToolkit.WinUI.TestDispatcher DispatcherQueue { get; } = new();
        public TestWindow AppWindow => this;
        public int Id => 1;
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
    public class AppViewModel
    {
        public List<Music> SongsSource { get; } = [];
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
        public SQLite.SQLiteAsyncConnection Connection => _dbConnection;
        private readonly SQLite.SQLiteAsyncConnection _dbConnection;
        private readonly AddFolderService addFolderService = new();
        public MusicDatabaseService(string path) => _dbConnection = new(path);
        public async Task InitializeAsync()
        {
            await _dbConnection.CreateTableAsync<Music>();
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
