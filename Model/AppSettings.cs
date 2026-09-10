using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Model
{
    public static class AppSettings
    {
        public static event EventHandler? OutputSettingsChanged;
        public static event EventHandler? OutputSettingsUpdated;
        public static event EventHandler? EqUpdated;
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
        public static string OutputMode { get; set; } = "DirectSound";
        public static int BassOutputDeviceId { get; set; } = -1;
        public static int BassASIODeviceId { get; set; } = 0;
        public static string DeviceName { get; set; } = ToolUtils.GetString("DefaultDevice");
        //public static double LyricsBlurAmount { get; set; } = 4;
        public static string AppStyle { get; set; } = "TransparentAcrylic";
        public static float CustomAcrylicOpacity { get; set; } = 0.5f;
        public static uint CustomColorArgb { get; set; } = 0xFF808080u;
        public static uint LyricsCustomColorRgb { get; set; } = 0x00FFFFFFu;
        public static string AppTheme { get; set; } = "Default";
        public static ElementTheme ElementTheme { get; set; } = ElementTheme.Default;
        public static bool IsRunningBackend { get; set; } = true;
        //public static bool IsDarkMode { get; set; } = false;
        public static bool IsAutoLyricsEnabled { get; set; } = true;
        public static bool IsDesktopLyricsEnabled { get; set; } = false;
        public static bool IsDesktopLyricsLocked { get; set; } = false;
        public static bool IsDesktopLyricsKaraokeEnabled { get; set; } = false;
        public static double DesktopLyricsFontSize { get; set; } = 36;
        public static string DesktopLyricsFontFamily { get; set; } = "Segoe UI";
        public static uint DesktopLyricsColorRgb { get; set; } = 0x00FFFFFFu;
        /// <summary>false（默认）= 桌面歌词颜色按悬浮窗周围环境自动取黑/白；true = 用户自选颜色覆盖自动取色。</summary>
        public static bool IsDesktopLyricsCustomColorEnabled { get; set; } = false;
        public static bool IsDesktopLyricsTranslationEnabled { get; set; } = true;
        public static bool IsDesktopLyricsGlowEnabled { get; set; } = true;
        public static bool IsDesktopLyricsCharFloatEnabled { get; set; } = true;
        public static bool IsDesktopLyricsCharScaleEnabled { get; set; } = true;
        public static double DesktopLyricsLongSyllableThreshold { get; set; } = 700.0;
        public static double DesktopLyricsGlowAmount { get; set; } = 5.0;
        public static double DesktopLyricsCharFloatAmount { get; set; } = 5.0;
        public static double DesktopLyricsCharScaleAmount { get; set; } = 110.0;
        /// <summary>桌面歌词阴影强度（0–100%）：0 = 关闭（渲染直接跳过阴影）；50 = 单层满强度；100 = 双重叠加。</summary>
        public static double DesktopLyricsShadowAmount { get; set; } = 50.0;
        public static int DesktopLyricsFontWeight { get; set; } = 400;
        public static int LyricsFontWeight { get; set; } = 700;
        public static bool IsAutoCoverEnabled { get; set; } = true;
        /// <summary>当前均衡器状态的 JSON 快照（EqPreset v1 结构，持久化用）。</summary>
        public static string EqualizerStr { get; set; } = string.Empty;
        /// <summary>运行时 10 段均衡器状态（含预留 Q 值），UI 与 IPC 的唯一数据源。</summary>
        public static EqBand[] EqualizerBands { get; set; } = EqualizerHelper.CreateDefaultBands();
        public static bool IsEqualizerEnabled { get; set; } = false;
        /// <summary>当前预设名：内置预设键、自定义预设名，或 "Custom"（手动调整中）。</summary>
        public static string EqualizerPreset { get; set; } = "Flat";
        public static bool IsCustomAppSize { get; set; } = false;
        public static int AppWidth { get; set; } = 1280;
        public static int AppHeight { get; set; } = 810;
        public static bool IsGlobalFontSizeEnabled { get; set; } = false;
        //public static double GlobalFontSize { get; set; } = 32;
        public static bool IsUpdateBackDrop { get; set; } = false;
        //public static CanvasHorizontalAlignment LyricsAlignment { get; set; } = CanvasHorizontalAlignment.Left;
        public static string MusicCoverCache { get; set; } = System.IO.Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "MusicCoverCache");
        public static ImageSwitchType ImageSwitchType = ImageSwitchType.ScaleInOut;
        public static bool EnableGlobalHotKey { get; set; } = false;
        public static bool IsTrimOnHideEnabled { get; set; } = false;
        public static bool IsTrimAfterPlaybackEnabled { get; set; } = false;
        private static string _artistSplitSymbols = ", ; / 、 & feat. :";
        private static string[] _artistSplitters = ParseArtistSplitSymbols(_artistSplitSymbols);
        public static string ArtistSplitSymbols
        {
            get => _artistSplitSymbols;
            set
            {
                _artistSplitSymbols = value ?? string.Empty;
                _artistSplitters = ParseArtistSplitSymbols(_artistSplitSymbols);
            }
        }
        public static string[] ArtistSplitters => _artistSplitters;
        private static string[] ParseArtistSplitSymbols(string symbols)
        {
            if (string.IsNullOrWhiteSpace(symbols))
                return [];

            var list = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var segment in symbols.Split(','))
            {
                if (segment.Length == 0)
                {
                    if (seen.Add(",")) list.Add(",");
                    continue;
                }
                foreach (var token in segment.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (seen.Add(token)) list.Add(token);
                }
            }
            return list.ToArray();
        }

    }
}
