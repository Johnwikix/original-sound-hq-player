using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings
{
    public sealed partial class CoverBackgroundSettingsControl : UserControl
    {
        public SettingsViewModel ViewModel { get; }

        public CoverBackgroundSettingsControl()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            Unloaded -= OnUnloaded;
        }
    }
}
