namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>
    /// 单首歌曲的汇总统计（达标收听次数与累计收听秒数）。
    /// </summary>
    public class SongPlayStat
    {
        public string Title { get; init; } = string.Empty;
        public string Artist { get; init; } = string.Empty;
        public string Album { get; init; } = string.Empty;

        /// <summary>达标（听满阈值比例）次数。</summary>
        public int PlayCount { get; init; }

        /// <summary>累计收听秒数。</summary>
        public double TotalDurationSeconds { get; init; }
    }
}