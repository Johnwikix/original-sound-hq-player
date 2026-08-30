using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Model
{
    public class SaveSettings
    {
        public string OutputMode { get; set; } = "DirectSound";
        public int Latency { get; set; } = 300;
        public int BassOutputDeviceId { get; set; } = -1;
        public int BassASIODeviceId { get; set; } = 0;
        public bool IsFadeEnabled { get; set; } = false;
        public string DeviceFriendlyName { get; set; } = ToolUtils.GetString("DefaultDevice");
        [JsonPropertyName("DefualtEntry")]
        public string DefaultEntry { get; set; } = "AddFolder";
        [JsonPropertyName("DefualtPlayList")]
        public string DefaultPlayList { get; set; } = "song";
        public string AppStyle { get; set; } = "TransparentAcrylic";
        public float CustomAcrylicOpacity { get; set; } = 0.5f;
        public uint CustomColorArgb { get; set; } = 0xFF808080u;
        public uint LyricsCustomColorRgb { get; set; } = 0x00FFFFFFu;
        public bool IsCustomLyricsColorEnabled { get; set; } = false;
        public string AppTheme { get; set; } = "Default";
        public float LyricsBlurAmount { get; set; } = 5f;
        public bool IsRunningBackend { get; set; } = true;
        public bool IsAutoLyricsEnabled { get; set; } = true;
        public bool IsAutoCoverEnabled { get; set; } = true;
        public bool IsDopEnabled { get; set; } = false;
        public int DsdGain { get; set; } = 6;
        public int DsdPcmFreq { get; set; } = 88200;
        public bool IsPlayDetailBtnVisible { get; set; } = true;
        public int CoverSize { get; set; } = 150;
        public AnimatedTextEffect Win2dTextEffectType { get; set; } = AnimatedTextEffect.TextDefaultEffect;
        public bool IsFluidBackgroundEnabled { get; set; } = true;
        public int BackgroundShader { get; set; } = 0;   // 0=Fluid, 1=PS3XMB, 2=GradientFlow, 3=WavyBackground, 4=ChromaticResonance (老版本 3/4/5 加载时迁移)
        public bool IsFogEffectEnabled { get; set; } = false;
        public bool IsSnowEffectEnabled { get; set; } = false;
        public bool IsRaindropEffectEnabled { get; set; } = false;
        public bool IsFolderWatchEnabled { get; set; } = true;
        public bool IsCustomAppSize { get; set; } = false;
        public int AppWidth { get; set; } = 1280;
        public int AppHeight { get; set; } = 810;
        public string GlobalFont { get; set; } = "Segoe UI, sans-serif";
        public bool IsGlobalFontSizeEnabled { get; set; } = false;
        public double GlobalFontSize { get; set; } = 32;
        public bool IsUpdateBackDrop { get; set; } = false;
        [JsonConverter(typeof(JsonStringEnumConverter<CanvasHorizontalAlignment>))]
        public CanvasHorizontalAlignment LyricsAlignment { get; set; } = CanvasHorizontalAlignment.Left;
        [JsonConverter(typeof(JsonStringEnumConverter<TextAlignment>))]
        public TextAlignment PlayingDetailAlignment { get; set; } = TextAlignment.Left;
        public bool UsePlayingDetailAlignmentInPortrait { get; set; } = false;
        public bool IsMusicInfoVisible { get; set; } = true;
        public int LyricsMargin { get; set; } = 20;
        public string MusicCoverCache { get; set; } = string.Empty;
        public bool UseImageDominantTheme { get; set; } = false;
        public bool EnableLightWave { get; set; } = true;
        public int PaletteAlgorithm { get; set; } = 0;
        public bool IsWin2dCoverImageControlEnable { get; set; } = false;
        public bool IsWin2dAnimatedText { get; set; } = false;
        public double CharFloatAmount { get; set; } = 5.0;
        public double CharScaleAmount { get; set; } = 110.0;
        public double GlowAmount { get; set; } = 5.0;
        public double LongSyllableThreshold { get; set; } = 700.0;
        public double PlayingLineTopOffsetPercent { get; set; } = 40.0;
        public double TranslatedOpacityPercent { get; set; } = 60.0;
        public double UnplayedOpacityPercent { get; set; } = 50.0;
        public double TargetFrameRate { get; set; } = 120.0;
        public bool EnableAdvancedLyricsEffect { get; set; } = false;
        [JsonConverter(typeof(JsonStringEnumConverter<EasingType>))]
        public EasingType ScrollEasingType { get; set; } = EasingType.Sine;
        [JsonConverter(typeof(JsonStringEnumConverter<EaseMode>))]
        public EaseMode ScrollEasingMode { get; set; } = EaseMode.Continuous;
        public bool IsLyricsMigrated { get; set; } = false;
        public List<string> PlayOrPauseShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "P" };
        public List<string> NextSongShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "Right" };
        public List<string> PreviousSongShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "Left" };
        public List<string> VolumeUpShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "Up" };
        public List<string> VolumeDownShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "Down" };
        public List<string> TogglePlayingDetailShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "Q" };
        public List<string> BackShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "B" };
        public List<string> ShowWindowShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "W" };
        public List<string> ToggleFullScreenShortcut { get; set; } = new List<string> { "Ctrl", "Alt", "F" };
        public bool EnableGlobalHotKey { get; set; } = false;
        public bool IsTrimOnHideEnabled { get; set; } = false;
        public bool IsTrimAfterPlaybackEnabled { get; set; } = false;
        public string ArtistSplitSymbols { get; set; } = ", ; / 、 & feat.";
        public bool IsDesktopLyricsEnabled { get; set; } = false;
        public bool IsDesktopLyricsLocked { get; set; } = false;
        public bool IsDesktopLyricsKaraokeEnabled { get; set; } = false;
        public double DesktopLyricsFontSize { get; set; } = 36;
        public string DesktopLyricsFontFamily { get; set; } = "Segoe UI";
        public uint DesktopLyricsColorRgb { get; set; } = 0x00FFFFFFu;
        public bool IsDesktopLyricsOutlineEnabled { get; set; } = true;
        public bool IsDesktopLyricsTranslationEnabled { get; set; } = true;
        public bool IsDesktopLyricsGlowEnabled { get; set; } = true;
        public bool IsDesktopLyricsCharFloatEnabled { get; set; } = true;
        public bool IsDesktopLyricsCharScaleEnabled { get; set; } = true;
        public int DesktopLyricsFontWeight { get; set; } = 400;
        public double DesktopLyricsOutlineWidth { get; set; } = 1.5;
        public int LyricsFontWeight { get; set; } = 700;
    }
}
