using SQLite;
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
        public string DefualtEntry { get; set; } = "AddFolder";
        public string DefualtPlayList { get; set; } = "song";
        public string AppStyle { get; set; } = "TransparentAcrylic";
        public float CustomAcrylicOpacity { get; set; } = 0.5f;
        public byte CustomColorAlpha { get; set; } = 255;
        public byte CustomColorRed { get; set; } = 128;
        public byte CustomColorGreen { get; set; } = 128;
        public byte CustomColorBlue { get; set; } = 128;
        public string AppTheme { get; set; } = "Default";
        public double LyricsBlurAmount { get; set; } = 4;
        public int MaxCoverCacheCount { get; set; } = 1000;
        public bool IsRunningBackend { get; set; } = true;
        public bool IsAutoLyricsEnabled { get; set; } = true;
        public bool IsDopEnabled { get; set; } = false;
        public int DsdGain { get; set; } = 6;
        public int DsdPcmFreq { get; set; } = 88200;        
        public bool IsPlayDetailBtnVisible { get; set; } = true;       
        public int CoverSize { get; set; } = 150;      
        public int EntranceAnimationTime { get; set; } = 300;
        public int SlideAnimationTime { get; set; } = 400;
        public int DrillInAnimationTime { get; set; } = 400;
        public bool IsBackgroundCoverEnabled { get; set; } = true;
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
        public bool IsWFWLyrics { get; set; } = true;
        public bool UseImageDominantTheme { get; set; } = false;
        public bool EnableLightWave { get; set; } = true;
        public bool IsWin2dCoverImageControlEnable { get; set; } = false;
        public bool IsWin2dAnimatedText { get; set; } = false;
    }
}
