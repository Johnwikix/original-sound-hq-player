using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class SavePlayState
    {
        public PlayMode PlayMode { get; set; } = PlayMode.ListLoop;
        public int? LastPlayedMusicId { get; set; }
        public double Volume { get; set; } = 50;
        public string SortOrder { get; set; } = "DefaultOrder";
        public bool HasWindowBounds { get; set; } = false;
        public int WindowX { get; set; } = 0;
        public int WindowY { get; set; } = 0;
        public int WindowWidth { get; set; } = 1280;
        public int WindowHeight { get; set; } = 810;
        public bool IsMaximized { get; set; } = false;
    }
}
