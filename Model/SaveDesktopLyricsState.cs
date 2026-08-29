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
    }
}
