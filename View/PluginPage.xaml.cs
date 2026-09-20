using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.Services.Plugins;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.View;

public sealed partial class PluginPage : Page
{
    public PluginPageViewModel ViewModel { get; } = App.Services.GetRequiredService<PluginPageViewModel>();
    public string? PluginId { get; private set; }
    public PluginPage() { InitializeComponent(); Unloaded += (_, _) => ViewModel.Dispose(); }
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is PluginRoute route) { PluginId = route.PluginId; ViewModel.Activate(route); }
    }
    protected override void OnNavigatedFrom(NavigationEventArgs e) { ViewModel.Dispose(); base.OnNavigatedFrom(e); }
}
