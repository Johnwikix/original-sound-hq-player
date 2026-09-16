using CommunityToolkit.Mvvm.ComponentModel;
namespace WinUIMusicPlayer.ViewModel
{
    public sealed record Music(int Id);
    public sealed class AppViewModel : ObservableObject
    {
        public Music? CurrentPlayingMusic { get; set => SetProperty(ref field, value); }
        public System.Collections.ObjectModel.ObservableCollection<Music> CurrentPlayingList { get; set => SetProperty(ref field, value); } = [];
        public bool IsPlaying { get; set; }
        public bool IsFolderWatchEnabled { get; set => SetProperty(ref field, value); } = true;
        public Microsoft.UI.Xaml.Visibility ProcessRingVisibility { get; set; }
    }
    public sealed class MusicBrowseViewModel
    {
        public Music? LastPlayed;
        public Task PlayMusic(Music music) { LastPlayed = music; return Task.CompletedTask; }
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class BassPlayerCommandService(WinUIMusicPlayer.ViewModel.AppViewModel state)
    {
        public int Toggles, NextCalls;
        public long Position;
        public TaskCompletionSource? Pending;
        public async Task PlayButton() { Toggles++; if (Pending is not null) await Pending.Task; state.IsPlaying = !state.IsPlaying; }
        public void PlayNextTrack() => NextCalls++;
        public void ChangeWaveChannelTime(long position) => Position = position;
    }
    public sealed record Folder(string Path);
    public sealed class MusicDatabaseService
    {
        public List<Folder> Folders = [];
        public int Reads;
        public TaskCompletionSource<List<Folder>>? Pending;
        public Task<List<Folder>> GetFolders() { Reads++; return Pending?.Task ?? Task.FromResult(Folders); }
    }
    public static class AudioFileWriteGate { public static bool IsOwnWriteEvent(string path) => false; }
    public static class AutoRescanService
    {
        public static int Scans;
        public static Func<CancellationToken, Task>? Scan;
        public static Task AutoScan(CancellationToken token)
        {
            Interlocked.Increment(ref Scans);
            return Scan?.Invoke(token) ?? Task.CompletedTask;
        }
    }
}
namespace Microsoft.UI.Xaml { public enum Visibility { Visible, Collapsed } }
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
        public WindowOptions AppWindow { get; } = new();
        public bool Visible;
        public void Activate() { }
    }
    public sealed class WindowOptions { public object? Presenter { get; set; } }
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
namespace WinUIEx
{
    public static class Extensions
    {
        public static void Show(this WinUIMusicPlayer.Window window) => window.Visible = true;
        public static void SetForegroundWindow(this WinUIMusicPlayer.Window window) { }
    }
}
namespace Microsoft.UI.Windowing
{
    public enum OverlappedPresenterState { Minimized, Restored }
    public sealed class OverlappedPresenter
    {
        public OverlappedPresenterState State;
        public void Restore() => State = OverlappedPresenterState.Restored;
    }
}
namespace WinUIMusicPlayer.DesktopLyrics
{
    public sealed class DesktopLyricsViewModel
    {
        public bool IsEnabled, IsKaraokeEnabled, IsLocked;
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
