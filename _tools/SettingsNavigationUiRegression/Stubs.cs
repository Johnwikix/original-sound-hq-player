using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinUIMusicPlayer
{
    internal static class App { public static IServiceProvider Services { get; set; } = null!; }
}
namespace WinUIMusicPlayer.Services
{
    internal static class UiLayoutDiagnostics { public static void Track(FrameworkElement element) { } }
}
namespace WinUIMusicPlayer.ViewModel
{
    public sealed class SettingsViewModel
    {
        public AppViewModel AppViewModel { get; } = new();
    }
    public sealed class AppViewModel
    {
        public bool IsRealDevceChange { get; set; }
        public int RefreshCount { get; private set; }
        public Task GetWasapiDeviceAsync() { RefreshCount++; return Task.CompletedTask; }
    }
}
namespace WinUIMusicPlayer.View.SubView.Settings
{
    public class ProbeSection : UserControl
    {
        public static List<ProbeSection> Created { get; } = [];
        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        protected ProbeSection()
        {
            Created.Add(this);
            Content = new TextBlock { Text = GetType().Name };
            Loaded += (_, _) => Loads++;
            Unloaded += (_, _) => Unloads++;
        }
    }
    public sealed class GeneralSettingsControl : ProbeSection { }
    public sealed class ShortcutsSettingsControl : ProbeSection { }
    public sealed class DspSettingsControl : ProbeSection { }
    public sealed class CoverBackgroundSettingsControl : ProbeSection { }
    public sealed class LyricsSettingsControl : ProbeSection { }
    public sealed class AboutSettingsControl : ProbeSection { }
}
