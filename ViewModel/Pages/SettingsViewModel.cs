using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SettingsViewModel : ObservableObject
    {
        public AppViewModel AppViewModel { get; }
        public WinUIMusicPlayer.State.AppState State => AppViewModel.State;
        public DesktopLyrics.DesktopLyricsViewModel DesktopLyrics { get; }

        public SettingsViewModel(AppViewModel appViewModel, DesktopLyrics.DesktopLyricsViewModel desktopLyrics)
        {
            AppViewModel = appViewModel;
            DesktopLyrics = desktopLyrics;
            // Both objects share the application lifetime; also reflect settings restored after construction.
            State.Preferences.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(State.Preferences.LocalLyricsFormatOrder))
                {
                    OnPropertyChanged(nameof(FirstLyricsFormatIndex));
                    OnPropertyChanged(nameof(SecondLyricsFormatIndex));
                    OnPropertyChanged(nameof(ThirdLyricsFormatIndex));
                    OnPropertyChanged(nameof(FourthLyricsFormatIndex));
                }
                else if (e.PropertyName == nameof(State.Preferences.PreferDatabaseLyrics))
                {
                    OnPropertyChanged(nameof(FirstLyricsSourceIndex));
                    OnPropertyChanged(nameof(SecondLyricsSourceIndex));
                }
            };
        }

        private static readonly string[] LyricsFormats = ["krc", "qrc", "lrc", "ttml"];
        public IReadOnlyList<string> LyricsFormatOptions { get; } = (string[])["KRC", "QRC", "LRC", "TTML"];
        public IReadOnlyList<string> LyricsSourceOptions { get; } =
            (string[])[ToolUtils.GetString("LyricsSourceFile"), ToolUtils.GetString("LyricsSourceDatabase")];

        public int FirstLyricsFormatIndex { get => GetLyricsFormatIndex(0); set => SetLyricsFormatIndex(0, value); }
        public int SecondLyricsFormatIndex { get => GetLyricsFormatIndex(1); set => SetLyricsFormatIndex(1, value); }
        public int ThirdLyricsFormatIndex { get => GetLyricsFormatIndex(2); set => SetLyricsFormatIndex(2, value); }
        public int FourthLyricsFormatIndex { get => GetLyricsFormatIndex(3); set => SetLyricsFormatIndex(3, value); }

        public int FirstLyricsSourceIndex
        {
            get => State.Preferences.PreferDatabaseLyrics ? 1 : 0;
            set
            {
                if (value is 0 or 1) State.Preferences.PreferDatabaseLyrics = value == 1;
            }
        }

        public int SecondLyricsSourceIndex
        {
            get => State.Preferences.PreferDatabaseLyrics ? 0 : 1;
            set
            {
                if (value is 0 or 1) State.Preferences.PreferDatabaseLyrics = value == 0;
            }
        }

        private int GetLyricsFormatIndex(int priority)
        {
            // 状态中保存的是规范化顺序；Span 枚举避免绑定取值时反复拆分字符串（.NET 10+）。
            ReadOnlySpan<char> order = State.Preferences.LocalLyricsFormatOrder.AsSpan();
            int position = 0;
            foreach (Range range in order.Split(','))
            {
                if (position++ != priority) continue;
                for (int index = 0; index < LyricsFormats.Length; index++)
                    if (order[range].SequenceEqual(LyricsFormats[index])) return index;
            }
            return -1;
        }

        private void SetLyricsFormatIndex(int priority, int selectedIndex)
        {
            if ((uint)selectedIndex >= LyricsFormats.Length || GetLyricsFormatIndex(priority) == selectedIndex) return;
            string[] order = State.Preferences.LocalLyricsFormatOrder.Split(',');
            int occupied = Array.IndexOf(order, LyricsFormats[selectedIndex]);
            if (occupied < 0) return;
            // 原子交换两项，其他优先级保持不变；所有下拉框共用同一份偏好状态。
            (order[priority], order[occupied]) = (order[occupied], order[priority]);
            State.Preferences.LocalLyricsFormatOrder = string.Join(',', order);
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
