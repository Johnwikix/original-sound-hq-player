using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static event EventHandler? OutputSettingsChanged;
        public static event EventHandler? OutputSettingsUpdated;
        public static event EventHandler? EqUpdated;
        public static event EventHandler<Dictionary<string, double>>? EqualizerChangedEvent;
        public static void OnOutputSettingsChanged()
        {
            OutputSettingsChanged?.Invoke(null, EventArgs.Empty);
        }

        public static void OnOutputSettingsUpdated()
        {
            OutputSettingsUpdated?.Invoke(null, EventArgs.Empty);
        }
        public static void OnEqUpdated()
        {
            EqUpdated?.Invoke(null, EventArgs.Empty);
        }
        public static void EqualizerChanged()
        {
            EqualizerChangedEvent?.Invoke(null, Equalizer);
        }
        public static string OutputMode { get; set; } = "DirectSound";
        public static int BassOutputDeviceId { get; set; } = -1;
        public static int BassASIODeviceId { get; set; } = 0;
        public static string DeviceName { get; set; } = ToolUtils.GetString("DefaultDevice");        
        public static double LyricsBlurAmount { get; set; } = 4;
        public static string AppStyle { get; set; } = "TransparentAcrylic";
        public static float CustomAcrylicOpacity { get; set; } = 0.5f;
        public static byte CustomColorAlpha { get; set; } = 255;
        public static byte CustomColorRed { get; set; } = 128;
        public static byte CustomColorGreen { get; set; } = 128;
        public static byte CustomColorBlue { get; set; } = 128;
        public static string AppTheme { get; set; } = "Default";
        public static ElementTheme ElementTheme { get; set; } = ElementTheme.Default;
        public static bool IsRunningBackend { get; set; } = true;
        public static bool IsDarkMode { get; set; } = false;
        public static bool IsAutoLyricsEnabled { get; set; } = true;
        public static string EqualizerStr { get; set; } = string.Empty;
        public static Dictionary<string, double> Equalizer { get; set; } = new()
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
        public static bool IsCustomAppSize { get; set; } = false;
        public static int AppWidth { get; set; } = 1280;
        public static int AppHeight { get; set; } = 810;
        public static FontFamily GlobalFont { get; set; } = new FontFamily("Segoe UI");
        public static bool IsGlobalFontSizeEnabled { get; set; } = false;
        public static double GlobalFontSize { get; set; } = 32;
        public static bool IsUpdateBackDrop { get; set; } = false;
        public static CanvasHorizontalAlignment LyricsAlignment { get; set; } = CanvasHorizontalAlignment.Left;
        public static string MusicCoverCache { get; set; } = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicCoverCache");

    }
}
