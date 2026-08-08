namespace WinUIMusicPlayer.Model.Stats
{
    /// <summary>时段活跃度柱状图单根柱子。</summary>
    public class HourlyActivityItem
    {
        public string TimeLabel { get; set; } = string.Empty;
        public int Count { get; set; }
        public double HeightPercentage { get; set; }
        public string TooltipText { get; set; } = string.Empty;
    }
}