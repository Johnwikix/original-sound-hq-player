using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View.SubView.Settings
{
    public sealed partial class GeneralSettingsControl : UserControl
    {
        public SettingsViewModel ViewModel { get; }

        public GeneralSettingsControl()
        {
            InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
        }
    }
}
