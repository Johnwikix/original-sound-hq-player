using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer
{
    internal static class App
    {
        public static SingleInstanceRegression.HostWindow? MainWindow { get; set; }
        public static IServiceProvider Services { get; set; } = null!;
        public static Task Current_Exit() => Task.CompletedTask;
    }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class AppViewModel { }
    public sealed class PlaybackCommands { }
}
namespace WinUIMusicPlayer.View
{
    public sealed class MainPage
    {
        public void NavigateToSettingsPage() { }
        public void NavigateToPlayingDetailPage() { }
    }
}
namespace WinUIMusicPlayer.DesktopLyrics
{
    public sealed class DesktopLyricsViewModel
    {
        public bool IsEnabled, IsKaraokeEnabled, IsLocked;
        public WinUIMusicPlayer.Model.DesktopLyricsMode Mode;
    }
    public static class DesktopLyricsManager { public static void ResetWindowBounds() { } }
}
namespace WinUIMusicPlayer.State
{
    public sealed class AppState
    {
        public AppLifecycle Lifecycle { get; } = new();
        public ShellState Shell { get; } = new();
    }
}
namespace WinUIMusicPlayer.Helper
{
    internal static class WindowHelper
    {
        public static int ForegroundRequests;
        // 仅替换最终前台切换：测试不会抢用户焦点。
        public static bool SetForegroundWindow(IntPtr window) { ForegroundRequests++; return true; }
    }
}
