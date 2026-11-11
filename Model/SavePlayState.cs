using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Model
{
    public class SavePlayState
    {
        /// <summary>获取或设置队列存档版本；旧 JSON 默认零。</summary>
        public int SchemaVersion { get; set; }
        /// <summary>获取或设置各出现项的固定版本。</summary>
        public int[]? QueueRequestedVersionIds { get; set; }
        /// <summary>获取或设置各出现项的来源限制。</summary>
        public int[]? QueueSourceScopes { get; set; }
        /// <summary>获取或设置来源歌单出现项。</summary>
        public int[]? QueuePlaylistEntryIds { get; set; }
        public PlayMode PlayMode { get; set; } = PlayMode.ListLoop;
        public int? LastPlayedMusicId { get; set; }
        public long[]? QueueEntryIds { get; set; }
        public long[]? QueueOrderIds { get; set; }
        public int[]? QueueMusicIds { get; set; }
        public long CurrentQueueEntryId { get; set; }
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
