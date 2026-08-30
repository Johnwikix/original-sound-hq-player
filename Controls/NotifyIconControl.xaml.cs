using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
using WinUIEx;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class NotifyIconControl : Microsoft.UI.Xaml.Controls.UserControl, IDisposable
    {
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        public NotifyIconControl()
        {
            this.InitializeComponent();
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            AppViewModel = App.Services.GetRequiredService<AppViewModel>();
            DataContext = this;
        }

        // 开关/锁定状态经 AppViewModel.IsDesktopLyricsEnabled/IsDesktopLyricsLocked 绑定到菜单图标（x:Bind），无需手动同步

        private void DesktopLyricsToggle_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.SetEnabled(!DesktopLyricsManager.IsEnabled);
        }

        private void DesktopLyricsLock_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.SetLocked(!DesktopLyricsManager.IsLocked);
        }

        private void ResetDesktopLyrics_Click(object sender, RoutedEventArgs e)
        {
            DesktopLyricsManager.ResetWindowBounds();
        }

        [RelayCommand]
        public async Task ExitApplication()
        {
            await App.Current_Exit();
        }

        private void NextSong_Click(object sender, RoutedEventArgs e)
        {
            MusicBrowseViewModel.NextMusicButton_Click();
        }

        private void PlayNPause_Click(object sender, RoutedEventArgs e)
        {
            MusicBrowseViewModel.PlayButton_Click();
        }


        private void LastSong_Click(object sender, RoutedEventArgs e)
        {
            MusicBrowseViewModel.LastMusicButton_Click();
        }

        [RelayCommand]
        public void ShowHideWindow()
        {
            ShowWindow(App.MainWindow);
        }

        [RelayCommand]
        public void ShowPlayingDetail()
        {
            ShowWindow(App.MainWindow);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                App.Services.GetRequiredService<MainPage>().NavigateToPlayingDetailPage();
            });
        }

        private void PlayMode_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as ToggleMenuFlyoutItem;
            if (menuItem is not null)
            {
                switch (menuItem.Name)
                {
                    case "IconRepeatAll":
                        AppViewModel.CurrentPlayMode = PlayMode.ListLoop;
                        AppViewModel.PlayModeFlyoutText = GetString("IconListLoop");
                        break;
                    case "IconRepeatOne":
                        AppViewModel.CurrentPlayMode = PlayMode.SingleLoop;
                        AppViewModel.PlayModeFlyoutText = GetString("IconSingleTuneCirculation");
                        break;
                    case "IconRepeatOff":
                        AppViewModel.CurrentPlayMode = PlayMode.RepeatOff;
                        AppViewModel.PlayModeFlyoutText = GetString("IconSinglePlayback");
                        break;
                    case "IconShuffle":
                        AppViewModel.CurrentPlayMode = PlayMode.RandomLoop;
                        AppViewModel.PlayModeFlyoutText = GetString("IconRandomLoop");
                        break;
                }
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            ShowWindow(App.MainWindow);
            App.Services.GetRequiredService<MainPage>()?.NavigateToSettingsPage();
        }

        private void ShowWindow(MainWindow window)
        {
            if (WindowHelper.IsWindowVisible(AppData.HWnd))
            {
                if (window.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                {
                    //window.Restore();
                    window.Activate();
                    window.SetForegroundWindow();
                }
            }
            else
            {
                if (window is null)
                {
                    return;
                }
                if (!window.Visible)
                {
                    window.Show();
                    window.InitializeTaskbarHelper();
                }
            }
        }

        private void VolumeUp_Click(object sender, RoutedEventArgs e)
        {
            AppViewModel.AdjustVolume(10);
        }

        private void VolumeDown_Click(object sender, RoutedEventArgs e)
        {
            AppViewModel.AdjustVolume(-10);
        }

        public void Dispose()
        {
            try
            {
                NotifyIcon?.Dispose();
                NotifyIcon = null;
            }
            catch (Exception)
            {
                NotifyIcon = null;
            }
        }
    }
}
