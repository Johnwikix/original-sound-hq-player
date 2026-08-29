using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/描边），与主界面歌词设置互不影响。</summary>
    public readonly record struct DesktopLyricsStyle(double FontSize, string FontFamily, Color Color, bool Outline);
}
