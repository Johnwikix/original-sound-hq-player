using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
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
        public byte CustomColorAlpha { get; set; } = 255;
        public byte CustomColorRed { get; set; } = 128;
        public byte CustomColorGreen { get; set; } = 128;
        public byte CustomColorBlue { get; set; } = 128;
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
        public bool IsFolderWatchEnabled { get; set; } = true;
        public bool IsCustomAppSize { get; set; } = false;
        public int AppWidth { get; set; } = 1280;
        public int AppHeight { get; set; } = 810;
        public string GlobalFont { get; set; } = "Segoe UI, sans-serif";
        public bool IsGlobalFontSizeEnabled { get; set; } = false;
        public double GlobalFontSize { get; set; } = 32;
        public bool IsUpdateBackDrop { get; set; } = false;
        public string LyricsAlignment { get; set; } = "Left";
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
        public double TargetFrameRate { get; set; } = 60.0;
        public bool EnableAdvancedLyricsEffect { get; set; } = false;
        public bool IsLyricsMigrated { get; set; } = false;
    }
}
