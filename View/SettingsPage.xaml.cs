using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.View
{
    /// <summary>
    /// 璁剧疆椤?椤堕儴 SelectorBar 鍒嗗尯鍒囨崲,鍚勫垎鍖烘壙杞戒簬 View/SubView/Settings 涓嬬殑 UserControl銆?
    /// </summary>
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel { get; }

        public SettingsPage()
        {
            InitializeComponent();
            WinUIMusicPlayer.Services.UiLayoutDiagnostics.Track(this);
            ViewModel = App.Services.GetRequiredService<SettingsViewModel>();
            DataContext = this;
            NavigationCacheMode = NavigationCacheMode.Disabled;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            ViewModel.AppViewModel.IsRealDevceChange = false;
            _ = ViewModel.AppViewModel.GetWasapiDeviceAsync();
        }
    }
}
