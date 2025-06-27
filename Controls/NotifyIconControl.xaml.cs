using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class NotifyIconControl : UserControl
    {
        //public event EventHandler playLastSong;
        //public event EventHandler playNextSong;
        //public event EventHandler playStop;
        //public event EventHandler<PlayMode> playModeEvent;
        private MusicBrowseViewModel _musicBrowseViewModel;
        public NotifyIconControl()
        {
            this.InitializeComponent();
            _musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
        }

        [RelayCommand]
        public void ExitApplication()
        {
            NotifyIcon.Dispose();
            App.MainWindow?.Close();
        }

        private void NextSong_Click(object sender, RoutedEventArgs e)
        {
            //playNextSong?.Invoke(this, EventArgs.Empty);
            _musicBrowseViewModel.NextMusicButton_Click();
        }

        private void PlayNPause_Click(object sender, RoutedEventArgs e)
        {
            //playStop?.Invoke(this, EventArgs.Empty);
            _musicBrowseViewModel.PlayButton_Click();
            UpdatePlayNPause();
        }

        public void UpdatePlayNPause() {
            if (AppSettings.isPlaying)
            {
                PlayNPauseIcon.Glyph = "\uE769"; // 播放图标
                PlayNPause.Text = GetString("IconPause");
            }
            else
            {
                PlayNPauseIcon.Glyph = "\uE768"; // 暂停图标
                PlayNPause.Text = GetString("IconPlay");
            }
        }

        private void LastSong_Click(object sender, RoutedEventArgs e)
        {
            _musicBrowseViewModel.LastMusicButton_Click();
            //playLastSong?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ShowHideWindow()
        {
            MainWindow window = App.MainWindow;
            ShowWindow(window);
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
                        PlayModeFlyoutIcon.Glyph = "\uE8EE";
                        PlayModeFlyout.Text = GetString("IconListLoop");
                        break;
                    case "IconRepeatOne":
                        UpdatePlayModeIcon(PlayMode.SingleLoop);
                        PlayModeFlyoutIcon.Glyph = "\uE8ED";
                        PlayModeFlyout.Text = GetString("IconSingleTuneCirculation");
                        break;
                    case "IconRepeatOff":
                        UpdatePlayModeIcon(PlayMode.RepeatOff);
                        PlayModeFlyoutIcon.Glyph = "\uF5E7";
                        PlayModeFlyout.Text = GetString("IconSinglePlayback");
                        break;
                    case "IconShuffle":
                        UpdatePlayModeIcon(PlayMode.RandomLoop);
                        PlayModeFlyoutIcon.Glyph = "\uE8B1";
                        PlayModeFlyout.Text = GetString("IconRandomLoop");
                        break;
                }
            }
            else
            {
                // 如果点击已选中的项，保持选中状态
                menuItem.IsChecked = true;
            }
        }

        private void UpdatePlayModeIcon(PlayMode playMode) {
            AppData.PlayMode = playMode;
            App.Services.GetRequiredService<MusicBrowseViewModel>().CurrentPlayMode = playMode;
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

        public void UpdatePlayMode() {
            var name = "IconRepeatOne";
            var iconStr = "\uE8EE";
            var flyoutText = GetString("IconSingleTuneCirculation");
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    name = "IconRepeatOne";
                    iconStr = "\uE8ED";
                    flyoutText = GetString("IconSingleTuneCirculation");
                    break;
                case PlayMode.ListLoop:
                    name = "IconRepeatAll";
                    iconStr = "\uE8EE";
                    flyoutText = GetString("IconListLoop");
                    break;
                case PlayMode.RandomLoop:
                    name = "IconShuffle";
                    iconStr = "\uE8B1";
                    flyoutText = GetString("IconRandomLoop");
                    break;
                case PlayMode.RepeatOff:
                    name = "IconRepeatOff";
                    iconStr = "\uF5E7";
                    flyoutText = GetString("IconSinglePlayback");
                    break;
            }
            PlayModeFlyoutIcon.Glyph = iconStr;
            PlayModeFlyout.Text = flyoutText;
            if (PlayModeFlyout != null)
            {
                foreach (var item in PlayModeFlyout.Items)
                {
                    if (item is ToggleMenuFlyoutItem toggleItem)
                    {
                        toggleItem.IsChecked = item.Name.ToString() == name ? true:false;
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

        private void ShowWindow(MainWindow window) {
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
            }
        }        
    }
}
