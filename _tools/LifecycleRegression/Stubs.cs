using CommunityToolkit.Mvvm.ComponentModel;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Model
{
    public sealed class Music(int id) : ObservableObject
    {
        public int Id { get; } = id;
        public int SongId { get; init; }
        public int SourceId { get; init; }
        public bool IsRemote { get; init; }
        public bool IsMissing { get; init; }
        public bool IsDetached { get; init; }
        public bool IsRemoteOffline { get; set => SetProperty(ref field, value); }
        public bool IsPlayable { get; set => SetProperty(ref field, value); } = true;
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel : ObservableObject
    {
        public Music? CurrentPlayingMusic { get; set => SetProperty(ref field, value); }
        public System.Collections.ObjectModel.ObservableCollection<Music> CurrentPlayingList { get; set => SetProperty(ref field, value); } = [];
        public int GetCurrentIndex() => CurrentPlayingList.ToList().FindIndex(music => music.Id == CurrentPlayingMusic?.Id);
        public bool IsPlaying { get; set; }
        public bool CanSeekDisplayedPlayback { get; set => SetProperty(ref field, value); } = true;
        public bool IsPlaybackEngineReady { get; set => SetProperty(ref field, value); }
        public bool IsFolderWatchEnabled { get; set => SetProperty(ref field, value); } = true;
        public ProgressCenter Progress { get; } = new();
        public WinUIMusicPlayer.State.AppState State { get; } = new();
        public Music? FindById(int id) => CurrentPlayingList.FirstOrDefault(music => music.Id == id);
        public IReadOnlyList<Music> FindVersions(int songId) => CurrentPlayingList.Where(music => music.SongId == songId).ToArray();
    }
    // 与真实 ProgressCenter 同签名的空桩：监视器只用到 Begin/Complete。
    public sealed class ProgressCenter
    {
        public static class Keys { public const string LibraryRescanning = nameof(LibraryRescanning); }
        public void Begin(string key, string text, double percent = -1) { }
        public void Report(string key, double percent) { }
        public void Complete(string key) { }
    }
    public sealed class MusicBrowseViewModel
    {
        public Music? LastPlayed;
        public Task PlayMusic(Music music) { LastPlayed = music; return Task.CompletedTask; }
    }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState
    {
        public PlaybackState Playback { get; } = new();
        public PlaybackQueueState Queue { get; } = new();
    }
    public sealed class PlaybackState { public object? PendingSelection { get; set; } }
    public sealed class PlaybackQueueState
    {
        public long CurrentEntryId => 1;
        public long EntryIdAt(int index) => index + 1L;
        public int RequestedVersion(long entryId) => 0;
        public int SourceScope(long entryId) => -1;
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class PlaybackCoordinator(WinUIMusicPlayer.ViewModel.AppViewModel state, WinUIMusicPlayer.ViewModel.MusicBrowseViewModel browse)
    {
        public int GetNavigationIndex() => state.GetCurrentIndex();
        public Task PlayAtAsync(int index, int direction) => browse.PlayMusic(state.CurrentPlayingList[index]);
        public Task PlayAsync(Music music, long entryId) => browse.PlayMusic(music);
        public Task ResumeCurrentAsync() => state.CurrentPlayingMusic is { } music ? browse.PlayMusic(music) : Task.CompletedTask;
        public void CancelPendingSelection() => state.State.Playback.PendingSelection = null;
    }
    public sealed class RemotePlaybackService
    {
        public bool WantsPlay { get; private set; }
        public bool NeedsStart { get; set; }
        public Task SetIntentAsync(bool playing) { WantsPlay = playing; return Task.CompletedTask; }
    }
    public static class SongVersionPolicy
    {
        internal static bool InScope(Music music, int sourceId) => !music.IsDetached &&
            (sourceId == -1 || sourceId == music.SourceId || sourceId == -2 && music.IsRemote);
    }
    public sealed class ShellService { public void ShowMainWindow() => App.MainWindow.Visible = true; }
    public sealed class BassPlayerCommandService(WinUIMusicPlayer.ViewModel.AppViewModel state)
    {
        public int Toggles, NextCalls;
        public long Position;
        public TaskCompletionSource? Pending;
        public async Task PlayButton() { Toggles++; if (Pending is not null) await Pending.Task; state.IsPlaying = !state.IsPlaying; }
        public void PlayNextTrack() => NextCalls++;
        public void ChangeWaveChannelTime(long position) => Position = position;
    }
    // 与生产 Model/Folder 对齐的最小桩：监视器跳过外部导入虚拟行时读取该属性。
    public sealed record Folder(string Path) { public bool IsExternalImport => false; }
    public sealed class MusicDatabaseService
    {
        public List<Folder> Folders = [];
        internal event Action? LocalFoldersChanged;
        internal void PublishFolderChange() => LocalFoldersChanged?.Invoke();
        public int Reads;
        public TaskCompletionSource<List<Folder>>? Pending;
        public Task<List<Folder>> GetFolders() { Reads++; return Pending?.Task ?? Task.FromResult(Folders); }
    }
    public static class AudioFileWriteGate { public static bool IsOwnWriteEvent(string path) => false; }
    public static class AutoRescanService
    {
        public static int Scans;
        public static Func<CancellationToken, Task>? Scan;
        internal static Task AutoScan(MusicDatabaseService database, Dictionary<string, WatchedFolderChanges?> changes, CancellationToken token)
        {
            Interlocked.Increment(ref Scans);
            return Scan?.Invoke(token) ?? Task.CompletedTask;
        }
    }
}
namespace Microsoft.UI.Xaml { public enum Visibility { Visible, Collapsed } }
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils
    {
        public static string GetString(string key) => key;
        public static bool IsMusicFile(string extension) => extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase);
    }
}
namespace WinUIMusicPlayer
{
    public static class App
    {
        public static readonly Window MainWindow = new();
        public static IServiceProvider Services = null!;
        public static int Exits;
        public static Task Current_Exit() { Exits++; return Task.CompletedTask; }
    }
    public sealed class Window
    {
        public Queue DispatcherQueue { get; } = new();
        public bool Visible;
    }
    public sealed class Queue
    {
        public int? OwnerThread;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Microsoft.UI.Dispatching.DispatcherQueueHandler> _pending = new();
        public bool HasThreadAccess => OwnerThread is null || OwnerThread == Environment.CurrentManagedThreadId;
        public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueueHandler action)
        {
            if (OwnerThread is null) action();
            else _pending.Enqueue(action);
            return true;
        }
        public void Drain()
        {
            if (!HasThreadAccess) throw new Exception("UI queue drained from worker thread");
            while (_pending.TryDequeue(out var action)) action();
        }
    }
}
namespace Microsoft.UI.Dispatching { public delegate void DispatcherQueueHandler(); }
namespace WinUIMusicPlayer.DesktopLyrics
{
    public sealed class DesktopLyricsViewModel
    {
        public bool IsEnabled, IsKaraokeEnabled, IsLocked;
        public DesktopLyricsMode Mode { get; set; }
    }
    public static class DesktopLyricsManager { public static void ResetWindowBounds() { } }
}
namespace WinUIMusicPlayer.View
{
    public sealed class MainPage
    {
        public int SettingsOpened;
        public void NavigateToSettingsPage() => SettingsOpened++;
        public void NavigateToPlayingDetailPage() { }
    }
}
