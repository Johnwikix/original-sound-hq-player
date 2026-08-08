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

        /// <summary>Top 专辑列表（按达标次数降序，其次累计收听秒）。</summary>
        public List<AlbumPlayStat> TopAlbums { get; set; } = [];

        /// <summary>时间段内活跃收听的天数（COUNT DISTINCT 本地日期）。</summary>
        public int ActiveDaysCount { get; set; }

        /// <summary>按开始收听的小时统计会话数（本地时区），长度 24。</summary>
        public int[] HourlyCounts { get; set; } = new int[24];

        /// <summary>按开始收听的本地日期统计会话数（热度图用，由同一分组扫描顺带聚合）。</summary>
        public Dictionary<DateTime, int> DailyCounts { get; set; } = [];
    }
}
