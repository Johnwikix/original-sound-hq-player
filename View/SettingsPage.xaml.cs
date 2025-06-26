using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// An empty page that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class SettingsPage : Page, INavigatable
    {
        public SettingsViewModel ViewModel { get; }
        public SettingsPage()
        {
            this.InitializeComponent();
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
        }

        public async void ReceiveNavigationParameter(object parameter)
        {
            Debug.WriteLine($"SettingsPage received parameter: {parameter}");
            await ToolUtils.RefreshDevice();
            LoadOutputDevices();
        }

        protected async override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            await ToolUtils.RefreshDevice();
            LoadOutputDevices();
        }

        private void LoadOutputDevices()
        {
            ViewModel.OutputDevices.Clear();
            foreach (string device in AppSettings.outputDeviceList)
            {
                ViewModel.OutputDevices.Add(device);
            }
            ViewModel.IsRealDevceChange = false;
            ViewModel.DeviceName = AppSettings.DeviceName;
        }

        private async void ToolTip_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            Uri uri = new Uri("https://docs.lrc.cx/docs/QuickStart/");
            await Launcher.LaunchUriAsync(uri);
        }
    }
}
