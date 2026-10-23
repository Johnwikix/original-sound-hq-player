using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel.Pages;

namespace WinUIMusicPlayer.View.SubView.Settings;

public sealed partial class LastFmSettingsControl : UserControl
{
    public LastFmSettingsViewModel ViewModel { get; }
    public LastFmSettingsControl()
    {
        ViewModel = App.Services.GetRequiredService<LastFmSettingsViewModel>();
        InitializeComponent();
    }
}
