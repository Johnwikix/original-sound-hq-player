using Microsoft.UI.Xaml;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static MMDeviceCollection OutputDevices { get; set; }
        public static string OutputMode { get; set; } = "WaveOut";
        public static int Latency { get; set; } = 400;

        public static event EventHandler OutputSettingsChanged;

        public static event EventHandler<Dictionary<string, double>> EqualizerChangedEvent;

        public static List<string> outputDeviceList = new List<string>();

        public static string DeviceName = "Default";
        public static string DefualtEntry { get; set; } = "AddFolder";
        public static string DefualtPlayList { get; set; } = "song";
        public static bool isPlaying { get; set; } = false;
        public static string LrcAPISource { get; set; } = "https://api.lrc.cx";
        public static string LrcAPIAuth { get; set; }
        public static bool isDsd { get; set; } = false;
        public static float dsdGain { get; set; } = 6f;
        public static string AppStyle { get; set; } = "TransparentAcrylic";
        public static string AppTheme { get; set; } = "Dark";
        public static ElementTheme elementTheme { get; set; } = ElementTheme.Default;
        public static bool isCoverCacheEnabled { get; set; } = true;
        //public static int maxCoverPreLoadNum { get; set; } = 100;
        public static bool isRunningBackend { get; set; } = true;
        public static bool isAutoLyricsEnabled { get; set; } = true;
        public static int CoverSize { get; set; } = 150; // 专辑封面大小，单位为像素
        public static int EntranceAnimationTime { get; set; } = 200;
        public static int SlideAnimationTime { get; set; } = 300;
        public static int DrillInAnimationTime { get; set; } = 400;
        public static bool IsProcessAboveNormal { get; set; } = false;
        public static bool IsBackgroundCoverEnabled { get; set; } = false; // 是否启用背景封面

        public static Dictionary<string, double> equalizer = new Dictionary<string, double>
        {
            {"32Hz", 0},   // 32Hz 初始增益 0dB
            {"64Hz", 0},   // 64Hz 初始增益 0dB
            {"125Hz", 0},  // 125Hz 初始增益 0dB
            {"250Hz", 0},  // 250Hz 初始增益 0dB
            {"500Hz", 0},  // 500Hz 初始增益 0dB
            {"1kHz", 0},   // 1kHz 初始增益 0dB
            {"2kHz", 0},   // 2kHz 初始增益 0dB
            {"4kHz", 0},   // 4kHz 初始增益 0dB
            {"8kHz", 0},   // 8kHz 初始增益 0dB
            {"16kHz", 0}   // 16kHz 初始增益 0dB
        };
        public static bool IsEqualizerEnabled { get; set; } = false;  
        public static string EqualizerPreset { get; set; } = "Flat";
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void EqualizerChanged()
        {
            EqualizerChangedEvent?.Invoke(null, equalizer);
        }
    }
}
