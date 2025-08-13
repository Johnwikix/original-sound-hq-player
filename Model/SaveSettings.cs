using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class SaveSettings
    {
        [PrimaryKey]
        public int Id { get; set; } = 1;
        public string OutputMode { get; set; } = "DirectSound";
        public int Latency { get; set; } = 400;
        public string Name { get; set; }
        public string DeviceFriendlyName { get; set; }
        public string DefualtEntry { get; set; }
        public string DefualtPlayList { get; set; }
        public string LrcAPISource { get; set; } = "https://api.lrc.cx";
        public string LrcAPIAuth { get; set; }
        public string AppStyle { get; set; } = "TransparentAcrylic";
        public string AppTheme { get; set; } = "Dark";
        public bool isCoverCacheEnabled { get; set; } = false;
        public bool isRunningBackend { get; set; } = true;
        public bool isAutoLyricsEnabled { get; set; } = true;
        public float dsdGain { get; set; } = 6f;
        public string equalizerStr { get; set; }
        public bool IsEqualizerEnabled { get; set; } = false;
        public int CoverSize { get; set; } = 150; // 专辑封面大小，单位为像素
        public string EqualizerPreset { get; set; } = "Flat";
        public int EntranceAnimationTime { get; set; } = 200;
        public int SlideAnimationTime { get; set; } = 300;
        public int DrillInAnimationTime { get; set; } = 400;
        public bool IsProcessAboveNormal { get; set; } = false;
        public bool IsBackgroundCoverEnabled { get; set; } = false; // 是否启用背景封面
        public bool IsFolderWatchEnabled { get; set; } = true;
        public int CoverLoadThreadCount { get; set; } = 8; // 专辑封面加载线程数
        public bool IsCustomAppSize { get; set; } = false;
        public int AppWidth { get; set; } = 1440;
        public int AppHeight { get; set; } = 810;
    }
}
