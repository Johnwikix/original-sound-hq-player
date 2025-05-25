using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class NotifyIconControl : UserControl
    {
        public event EventHandler playLastSong;
        public event EventHandler playNextSong;
        public event EventHandler playStop;
        public event EventHandler<PlayMode> playModeEvent;
        public NotifyIconControl()
        {
            this.InitializeComponent();
        }

        [RelayCommand]
        public void ExitApplication()
        {
            NotifyIcon.Dispose();
            App.MainWindow?.Close();
        }

        private void NextSong_Click(object sender, RoutedEventArgs e)
        {
            playNextSong?.Invoke(this, EventArgs.Empty);
        }

        private void PlayNPause_Click(object sender, RoutedEventArgs e)
        {
            playStop?.Invoke(this, EventArgs.Empty);
        }

        private void LastSong_Click(object sender, RoutedEventArgs e)
        {
            playLastSong?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        public void ShowHideWindow()
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
                var window = App.MainWindow;
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
                        playModeEvent?.Invoke(this,PlayMode.ListLoop);
                        PlayModeFlyoutIcon.Glyph = "\uE8EE";
                        PlayModeFlyout.Text = "列表循环";
                        break;
                    case "IconRepeatOne":
                        playModeEvent?.Invoke(this, PlayMode.SingleLoop);
                        PlayModeFlyoutIcon.Glyph = "\uE8ED";
                        PlayModeFlyout.Text = "单曲循环";
                        break;
                    case "IconRepeatOff":
                        playModeEvent?.Invoke(this, PlayMode.RepeatOff);
                        PlayModeFlyoutIcon.Glyph = "\uF5E7";
                        PlayModeFlyout.Text = "单曲播放";
                        break;
                    case "IconShuffle":
                        playModeEvent?.Invoke(this, PlayMode.RandomLoop);
                        PlayModeFlyoutIcon.Glyph = "\uE8B1";
                        PlayModeFlyout.Text = "随机循环";
                        break;
                }
            }
            else
            {
                // 如果点击已选中的项，保持选中状态
                menuItem.IsChecked = true;
            }
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
            var flyoutText = "单曲循环";
            switch (AppData.PlayMode)
            {
                case PlayMode.SingleLoop:
                    name = "IconRepeatOne";
                    iconStr = "\uE8ED";
                    flyoutText = "单曲循环";
                    break;
                case PlayMode.ListLoop:
                    name = "IconRepeatAll";
                    iconStr = "\uE8EE";
                    flyoutText = "列表循环";
                    break;
                case PlayMode.RandomLoop:
                    name = "IconShuffle";
                    iconStr = "\uE8B1";
                    flyoutText = "随机循环";
                    break;
                case PlayMode.RepeatOff:
                    name = "IconRepeatOff";
                    iconStr = "\uF5E7";
                    flyoutText = "单曲播放";
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
    }
}
