using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using WinUIMusicPlayer.Services.Lyrics;

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
                if (e.PropertyName == nameof(State.Preferences.LocalLyricsFormatOrder)) OnPropertyChanged(nameof(SelectedLyricsOrder));
            };
        }

        public sealed record LyricsOrderOption(string Value, string Label);
        public IReadOnlyList<LyricsOrderOption> LyricsOrderOptions { get; } = BuildLyricsOrders();
        public LyricsOrderOption SelectedLyricsOrder
        {
            get => LyricsOrderOptions.First(option => option.Value == LyricsFilePolicy.NormalizeOrder(State.Preferences.LocalLyricsFormatOrder));
            set
            {
                if (value is null || value.Value == State.Preferences.LocalLyricsFormatOrder) return;
                State.Preferences.LocalLyricsFormatOrder = value.Value;
                OnPropertyChanged();
            }
        }
        private static IReadOnlyList<LyricsOrderOption> BuildLyricsOrders()
        {
            string[] formats = ["krc", "qrc", "lrc", "ttml"];
            var result = new List<LyricsOrderOption>();
            foreach (string first in formats)
                foreach (string second in formats)
                    foreach (string third in formats)
                    {
                        if (first == second || first == third || second == third) continue;
                        string fourth = formats.First(value => value != first && value != second && value != third);
                        string value = $"{first},{second},{third},{fourth}";
                        result.Add(new(value, value.Replace(",", " → ").ToUpperInvariant()));
                    }
            return result;
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
