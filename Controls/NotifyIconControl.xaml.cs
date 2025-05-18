using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;
using H.NotifyIcon.Core;
using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using System.Diagnostics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    public sealed partial class NotifyIconControl : UserControl
    {
        public event EventHandler playLastSong;
        public event EventHandler playNextSong;
        public event EventHandler playStop;
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
                }
            }
            
        }
    }
}
