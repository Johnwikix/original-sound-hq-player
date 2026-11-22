using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings;

public sealed partial class LyricsEffectsSettingsControl : UserControl
{
    public AppViewModel ViewModel { get; }

    public LyricsEffectsSettingsControl()
    {
        ViewModel = App.Services.GetRequiredService<AppViewModel>();
        InitializeComponent();
    }
}
