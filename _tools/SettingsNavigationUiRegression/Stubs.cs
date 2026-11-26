using CommunityToolkit.WinUI.Controls;
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
        internal string? LastSectionTag { get; set; }

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
    public partial class ProbeSection : UserControl
    {
        public static List<ProbeSection> Created { get; } = [];
        public int Loads { get; private set; }
        public int Unloads { get; private set; }
        internal SettingsExpander Expander { get; } = new() { Header = "Fixture expander" };
        internal ScrollViewer Scroll { get; } = new();
        protected ProbeSection(bool expanded = false)
        {
            Created.Add(this);
            Expander.IsExpanded = expanded;
            Expander.Items.Add(new SettingsCard { Header = "Fixture nested card" });
            Scroll.Content = new StackPanel { Children = { Expander, new Border { Height = 2400 } } };
            Content = Scroll;
            Loaded += (_, _) => Loads++;
            Unloaded += (_, _) => Unloads++;
        }
    }
    public sealed partial class GeneralSettingsControl : ProbeSection { }
    public sealed partial class ShortcutsSettingsControl : ProbeSection { }
    public sealed partial class DspSettingsControl : ProbeSection { }
    public sealed partial class CoverBackgroundSettingsControl : ProbeSection { }
    public sealed partial class LyricsSettingsControl : ProbeSection { }
    public sealed partial class AboutSettingsControl() : ProbeSection(expanded: true) { }
}
