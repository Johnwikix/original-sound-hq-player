using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.ViewModel.Controls;

namespace WinUIMusicPlayer.View.SubView.Settings
{
    public sealed partial class GeneralSettingsControl : UserControl
    {
        public SettingsViewModel ViewModel { get; }
        public PlaybackTransitionsViewModel PlaybackTransitions { get; }

        public GeneralSettingsControl()
        {
            InitializeComponent();
            WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            PlaybackTransitions = App.Services.GetRequiredService<PlaybackTransitionsViewModel>();
            DataContext = this;
        }
    }
}
