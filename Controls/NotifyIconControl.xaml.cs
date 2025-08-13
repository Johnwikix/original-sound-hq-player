using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class NotifyIconControl : Microsoft.UI.Xaml.Controls.UserControl
    {
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        public NotifyIconControl()
        {
            this.InitializeComponent();
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            DataContext = this;
        }

        [RelayCommand]
        public void ExitApplication()
        {
            App.Current_Exit();
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
            MainWindow window = App.MainWindow;
            ShowWindow(window);
        }

        [RelayCommand]
        public void ShowPlayingDetail()
        {
            MainWindow window = App.MainWindow;
            ShowWindow(window);
            window.NavigateToMusicBrowsePage();
            _ = Task.Delay(100).ContinueWith(_ =>
            {
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicBrowseViewModel.ShowPlayingDetail();
                });
            });
        }

        private void PlayMode_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as ToggleMenuFlyoutItem;
            Debug.WriteLine(menuItem?.Name.ToString());
            if (menuItem != null && menuItem.IsChecked == true)
            {
                UncheckOtherItems(menuItem);

                // 触发事件通知播放模式变更
                switch (menuItem.Name)
                {
                    case "IconRepeatAll":
                        UpdatePlayModeIcon(PlayMode.ListLoop);
                        MusicBrowseViewModel.PlayModeFlyoutText = GetString("IconListLoop");
                        break;
                    case "IconRepeatOne":
                        UpdatePlayModeIcon(PlayMode.SingleLoop);
                        MusicBrowseViewModel.PlayModeFlyoutText = GetString("IconSingleTuneCirculation");
                        break;
                    case "IconRepeatOff":
                        UpdatePlayModeIcon(PlayMode.RepeatOff);
                        MusicBrowseViewModel.PlayModeFlyoutText = GetString("IconSinglePlayback");
                        break;
                    case "IconShuffle":
                        UpdatePlayModeIcon(PlayMode.RandomLoop);
                        MusicBrowseViewModel.PlayModeFlyoutText = GetString("IconRandomLoop");
                        break;
                }
                MusicBrowseViewModel._musicPlaybackService.UpdateCurrentPlayList();
            }
            else
            {
                // 如果点击已选中的项，保持选中状态
                menuItem.IsChecked = true;
            }
        }

        private void UpdatePlayModeIcon(PlayMode playMode)
        {
            AppData.PlayMode = playMode;
            MusicBrowseViewModel.CurrentPlayMode = playMode;
        }

        private void UncheckOtherItems(ToggleMenuFlyoutItem currentItem)
        {
            if (PlayModeFlyout != null)
            {
                // 遍历所有子项，取消其他 ToggleMenuFlyoutItem 的选中状态
                foreach (var item in PlayModeFlyout.Items)
                {
                    if (item is ToggleMenuFlyoutItem toggleItem && item != currentItem)
                    {
                        toggleItem.IsChecked = false;
                    }
                }
            }

        }

        public void UpdatePlayMode()
        {
            var name = "IconRepeatOne";
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    name = "IconRepeatOne";
                    break;
                case PlayMode.ListLoop:
                    name = "IconRepeatAll";
                    break;
                case PlayMode.RandomLoop:
                    name = "IconShuffle";
                    break;
                case PlayMode.RepeatOff:
                    name = "IconRepeatOff";
                    break;
            }
            if (PlayModeFlyout != null)
            {
                foreach (var item in PlayModeFlyout.Items)
                {
                    if (item is ToggleMenuFlyoutItem toggleItem)
                    {
                        toggleItem.IsChecked = item.Name.ToString() == name ? true : false;
                    }
                }
            }
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            MainWindow window = App.MainWindow;
            ShowWindow(window);
            if (window != null)
            {
                window.NavigateToSettingsPage();
            }
        }

        private void ShowWindow(MainWindow window)
        {
            if (WindowHelper.IsWindowVisible(AppData.m_hWnd))
            {
                // 如果窗口最小化，则恢复它
                if (WindowHelper.IsIconic(AppData.m_hWnd))
                {
                    WindowHelper.ShowWindow(AppData.m_hWnd, WindowHelper.SW_RESTORE);
                }
                // 将窗口置于前台
                WindowHelper.SetForegroundWindow(AppData.m_hWnd);
            }
            else
            {
                if (window == null)
                {
                    return;
                }
                if (!window.Visible)
                {
                    window.Show();
                    window.InitializeTaskbarHelper();
                }
                if (AppSettings.IsProcessAboveNormal)
                {
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
                }
            }
        }

        private void VolumeUp_Click(object sender, RoutedEventArgs e)
        {
            MusicBrowseViewModel.AdjustVolume(10);
        }

        private void VolumeDown_Click(object sender, RoutedEventArgs e)
        {
            MusicBrowseViewModel.AdjustVolume(-10);
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
