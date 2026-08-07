using System;
using System.Collections.Generic;

namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>
    /// 统计页单次刷新所需的数据快照：一次数据库查询 + 内存聚合生成，
    /// 避免对同一时间范围做多次重复的全表查询。
    /// </summary>
    public class StatsSnapshot
    {
        /// <summary>时间段内累计收听秒数（每首按 min(收听, 总长) 钳制）。</summary>
        public double TotalListeningSeconds { get; set; }

        /// <summary>达标（听满阈值比例）会话数。</summary>
        public int TracksPlayedCount { get; set; }

        /// <summary>Top 歌曲列表（按达标次数降序，其次累计收听秒）。</summary>
        public List<SongPlayStat> TopSongs { get; set; } = [];

        /// <summary>Top 歌手列表（按达标次数降序，其次累计收听秒）。</summary>
        public List<ArtistPlayStat> TopArtists { get; set; } = [];

        /// <summary>最常播放专辑名（无数据时为空字符串）。</summary>
        public string TopAlbumName { get; set; } = string.Empty;

        /// <summary>最常播放专辑达标次数。</summary>
        public int TopAlbumPlayCount { get; set; }

        /// <summary>最常播放专辑最近一次会话的歌曲 Id（播放锚点，歌曲可能已删除）。</summary>
        public int TopAlbumMusicId { get; set; }

        /// <summary>按开始收听的小时统计会话数（本地时区），长度 24。</summary>
        public int[] HourlyCounts { get; set; } = new int[24];
    }
}
