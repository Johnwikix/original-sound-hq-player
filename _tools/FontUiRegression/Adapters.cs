namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static Microsoft.UI.Xaml.ElementTheme ElementTheme { get; set; }
        public static string ArtistSplitSymbols => ";";
        public static bool AutoHideDesktopLyricsFloatingOnPlayingDetail { get; set; } = false;
        public static bool AutoHideDesktopLyricsOnPlayingDetail { get; set; } = false;
        public static bool AutoHideDesktopLyricsTaskbarOnPlayingDetail { get; set; } = false;
        public static double DesktopLyricsCharFloatAmount { get; set; } = 5.0;
        public static double DesktopLyricsCharScaleAmount { get; set; } = 110.0;
        public static uint DesktopLyricsColorRgb { get; set; } = 0x00FFFFFFu;
        public static double DesktopLyricsFontSize { get; set; } = 36;
        public static int DesktopLyricsFontWeight { get; set; } = 400;
        public static double DesktopLyricsGlowAmount { get; set; } = 5.0;
        public static double DesktopLyricsLongSyllableThreshold { get; set; } = 700.0;
        public static string DesktopLyricsMode { get; set; } = nameof(WinUIMusicPlayer.Model.DesktopLyricsMode.Floating);
        public static double DesktopLyricsShadowAmount { get; set; } = 50.0;
        public static bool IsDesktopLyricsCharFloatEnabled { get; set; } = true;
        public static bool IsDesktopLyricsCharScaleEnabled { get; set; } = true;
        public static bool IsDesktopLyricsCustomColorEnabled { get; set; } = false;
        public static bool IsDesktopLyricsEnabled { get; set; } = false;
        public static bool IsDesktopLyricsGlowEnabled { get; set; } = true;
        public static bool IsDesktopLyricsKaraokeEnabled { get; set; } = true;
        public static bool IsDesktopLyricsLocked { get; set; } = false;
    }
}
namespace WinUIMusicPlayer.Utils
{
    public static class ToolUtils { public static string GetString(string key) => key; }
}
namespace WinUIMusicPlayer.Services.Lyrics
{
    // Font tests never edit the unrelated lyrics-file priority.
    public static class LyricsFilePolicy { public static string NormalizeOrder(string order) => order; }
}

namespace WinUIMusicPlayer
{
    public static class App { public static System.IServiceProvider Services { get; set; } = null!; }
}

namespace WinUIMusicPlayer.State
{
    public sealed class AppState(PreferencesState preferences)
    {
        public PreferencesState Preferences { get; } = preferences;
        public DesktopLyricsState DesktopLyrics { get; } = new();
        public WinUIMusicPlayer.Services.AppLifecycle Lifecycle { get; } = new();
    }
}
namespace WinUIMusicPlayer.Services
{
    public sealed class MusicDatabaseService
    {
        public Task SaveSettingAsync() => Task.CompletedTask;
        public WinUIMusicPlayer.Model.SaveDesktopLyricsState LoadDesktopLyricsState() => new();
        public void SaveDesktopLyricsState(WinUIMusicPlayer.Model.SaveDesktopLyricsState state) { }
    }
    public sealed class ShutdownCoordinator : IDisposable
    {
        private readonly List<Action> _cleanup = [];
        public void RegisterCleanup(Action action) => _cleanup.Add(action);
        public void Dispose()
        {
            foreach (var cleanup in _cleanup) cleanup();
            _cleanup.Clear();
        }
    }
}
namespace WinUIMusicPlayer.DesktopLyrics
{
    public static class DesktopLyricsManager
    {
        public static void CloseWindow() { }
        public static void RecreateForMode() { }
        public static void SetWindowVisible(bool visible) { }
    }
}
