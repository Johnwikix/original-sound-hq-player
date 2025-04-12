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
        public static string DefualtEntry { get; set; } = "文件夹选择";
        public static string DefualtPlayList { get; set; } = "歌曲";
        public static bool isPlaying { get; set; } = false;
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
