using SQLite;
using System;

namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>
    /// 单次播放会话记录（播放统计系统底层数据）。
    /// 每次开始播放一首歌都会写入一行；收听时长在播放期间按秒累计，
    /// 切换歌曲 / 停止 / 退出应用时结算 EndedAt 与 DurationPlayedMs。
    /// 歌曲元数据做冗余快照，即使歌曲从库中删除，历史统计依然保留。
    /// </summary>
    public class PlaybackHistory
    {
        [PrimaryKey, AutoIncrement]
        public int Id { get; set; }
        public int MusicId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Album { get; set; } = string.Empty;

        /// <summary>歌曲总时长（毫秒）。</summary>
        public double TotalDurationMs { get; set; }

        /// <summary>本次会话实际收听时长（毫秒），已按歌曲总时长钳制。</summary>
        public double DurationPlayedMs { get; set; }

        /// <summary>会话开始时间（UTC）。</summary>
        public DateTime StartedAt { get; set; }

        /// <summary>会话结束时间（UTC）。</summary>
        public DateTime EndedAt { get; set; }
    }
}