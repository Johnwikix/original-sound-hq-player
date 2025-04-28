using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class SaveSettings
    {
        [PrimaryKey]
        public int Id { get; set; } = 1;
        public string OutputMode { get; set; } = "WasapiExclusive";
        public int Latency { get; set; } = 200;
        public string Name { get; set; }
        public string DeviceFriendlyName { get; set; }
        public string DefualtEntry { get; set; }
        public string DefualtPlayList { get; set; }
        public string LrcAPISource { get; set; }
        public string LrcAPIAuth { get; set; }
        public string AppStyle { get; set; }
        public string AppTheme { get; set; }
        public bool isCoverCacheEnabled { get; set; } = false;
        public int maxCoverPreLoadNum { get; set; } = 100;
        public bool isRunningBackend { get; set; } = true;
        public bool isAutoLyricsEnabled { get; set; } = false;
    }
}
