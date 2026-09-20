using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.Services.Plugins;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.View.SubView.Settings;

public sealed partial class PluginsSettingsControl : UserControl
{
    public PluginsViewModel ViewModel { get; } = App.Services.GetRequiredService<PluginsViewModel>();
    public PluginsSettingsControl() => InitializeComponent();
    private void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PluginItem item && ViewModel.ToggleCommand.CanExecute(item))
            ViewModel.ToggleCommand.Execute(item);
    }
    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PluginItem item && ViewModel.RetryCommand.CanExecute(item))
            ViewModel.RetryCommand.Execute(item);
    }
}
