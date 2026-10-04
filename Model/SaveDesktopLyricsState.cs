namespace WinUIMusicPlayer.Model
{
    /// <summary>桌面歌词窗口边界状态（独立于 Settings.json，比照 PlayState）。</summary>
    public class SaveDesktopLyricsState
    {
        public bool HasBounds { get; set; } = false;
        public int X { get; set; } = -1;
        public int Y { get; set; } = -1;
        public int Width { get; set; } = 1800;
        public int Height { get; set; } = 280;

        /// <summary>任务栏宿主的相对客户区布局；与悬浮窗的屏幕坐标分开保存。</summary>
        public bool TaskbarHasBounds { get; set; }
        public int TaskbarX { get; set; }
        public int TaskbarY { get; set; }
        public int TaskbarWidth { get; set; } = 960;
        public int TaskbarHeight { get; set; }
    }
}
