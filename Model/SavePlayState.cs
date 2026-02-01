using SQLite;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class SavePlayState
    {
        [PrimaryKey]
        public int Id { get; set; } = 1; // 固定 ID 为 1，方便管理
        public PlayMode PlayMode { get; set; }
        public int? LastPlayedMusicId { get; set; }
        public double Volume { get; set; } = 0.5;
        public string sortOrder { get; set; } = "DefaultOrder";
    }
}
