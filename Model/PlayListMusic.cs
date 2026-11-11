using SQLite;

namespace WinUIMusicPlayer.Model
{
    public class PlayListMusic
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int PlayListId { get; set; }
        public int MusicId { get; set; }
        /// <summary>获取或设置共享歌曲身份；MusicId 保留原始来源信息。</summary>
        public int SongId { get; set; }
        /// <summary>获取或设置固定版本；空值表示跟随歌曲策略。</summary>
        public int? RequestedMusicId { get; set; }
        public int Order { get; set; }
    }
}
