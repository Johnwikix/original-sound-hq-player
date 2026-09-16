using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using System;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SettingsViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public DesktopLyrics.DesktopLyricsViewModel DesktopLyrics { get; }

        public SettingsViewModel(AppViewModel appViewModel, DesktopLyrics.DesktopLyricsViewModel desktopLyrics)
        {
            AppViewModel = appViewModel;
            DesktopLyrics = desktopLyrics;
        }

        public Visibility CheckSystemVersion()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Minor == 0)
            {
                // 获取内部版本号（Build）
                int buildNumber = Environment.OSVersion.Version.Build;
                if (buildNumber >= 22000)
                {
                    return Visibility.Visible;
                }
                else
                {
                    return Visibility.Collapsed;
                }
            }
            return Visibility.Collapsed;
        }
    }
}
