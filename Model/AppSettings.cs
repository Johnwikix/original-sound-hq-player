using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static MMDeviceCollection OutputDevices { get; set; }
        public static string OutputMode { get; set; } = "DirectSound";
        public static int Latency { get; set; } = 400;

        public static event EventHandler OutputSettingsChanged;

        public static List<string> outputDeviceList = new List<string>();

        public static string DeviceName = "Default";
        public static string DefualtEntry { get; set; } = "AddFolder";
        public static string DefualtPlayList { get; set; } = "song";
        public static bool isPlaying { get; set; } = false;
        public static string LrcAPISource { get; set; } = "https://api.lrc.cx";
        public static string LrcAPIAuth { get; set; }
        //public static bool isDsd { get; set; } = false;
        public static string AppStyle { get; set; } = "TransparentAcrylic";
        public static string AppTheme { get; set; } = "Dark";
        public static ElementTheme elementTheme { get; set; } = ElementTheme.Default;
        public static bool isCoverCacheEnabled { get; set; } = true;
        public static int maxCoverPreLoadNum { get; set; } = 100;
        public static bool isRunningBackend { get; set; } = true;
        public static bool isAutoLyricsEnabled { get; set; } = true;
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
