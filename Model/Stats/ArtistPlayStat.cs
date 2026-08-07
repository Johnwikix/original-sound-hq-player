namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>
    /// 单个歌手/艺术家的汇总统计（达标收听次数与累计收听秒数）。
    /// </summary>
    public class ArtistPlayStat
    {
        public string Artist { get; init; } = string.Empty;

        /// <summary>达标（听满阈值比例）次数。</summary>
        public int PlayCount { get; init; }

        /// <summary>累计收听秒数。</summary>
        public double TotalDurationSeconds { get; init; }
    }
}