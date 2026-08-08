using System;

namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>热度图单个日期格子。</summary>
    public class HeatmapNode
    {
        public DateTime Date { get; set; }
        public int PlayCount { get; set; }
        public int Level { get; set; }
        public bool IsEmpty { get; set; }

        /// <summary>格子不透明度：空 0，无播放 0.05，按 1-4 级递增到 1.0。</summary>
        public double Opacity => IsEmpty ? 0.0 : Level == 0 ? 0.05 : Level * 0.25;
    }
}