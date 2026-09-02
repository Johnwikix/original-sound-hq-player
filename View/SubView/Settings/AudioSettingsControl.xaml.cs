using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.System;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings
{
    public sealed partial class AudioSettingsControl : UserControl
    {
        public SettingsViewModel ViewModel { get; }

        public AudioSettingsControl()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
        }

        private void SpectrumVisualization_Click(object sender, RoutedEventArgs e)
        {
            string storeUri = "spectrumvisualization:";
            LauncherOptions options = new LauncherOptions
            {
                FallbackUri = new Uri("ms-windows-store://pdp/?ProductId=9PL2DSHJ79W7")
            };
            _ = Launcher.LaunchUriAsync(new Uri(storeUri), options);
        }
    }
}
