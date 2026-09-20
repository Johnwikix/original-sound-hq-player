using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.State;

/// <summary>跨页面共享入口。子状态身份稳定，不持有页面、窗口或后台任务。</summary>
public sealed class AppState(AppLifecycle lifecycle)
{
    public AppLifecycle Lifecycle { get; } = lifecycle;
    public DesktopLyricsState DesktopLyrics { get; } = new();
    public PlaybackQueueState Queue { get; } = new();
    public LibraryViewState LibraryViews { get; } = new();
    public LibraryState Library { get; } = new();
    public HotKeyState HotKeys { get; } = new();
    public ShellState Shell { get; } = new();
    public PlaybackPresentationState Presentation { get; } = new();
    public PlaybackState Playback { get; } = new();
    public PreferencesState Preferences { get; } = new();
    public BrowseState Browse { get; } = new();
    public OutputState Output { get; } = new();
    public WinUIMusicPlayer.ViewModel.ProgressCenter Operations { get; } = new();
}
