using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class SavePlayState
    {
        public PlayMode PlayMode { get; set; } = PlayMode.ListLoop;
        public int? LastPlayedMusicId { get; set; }
        public double Volume { get; set; } = 50;
        public string SortOrder { get; set; } = "DefaultOrder";
    }
}
