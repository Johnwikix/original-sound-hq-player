using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/描边/字重/描边宽度/翻译/逐字动效），与主界面歌词设置互不影响。</summary>
    public readonly record struct DesktopLyricsStyle(
        double FontSize,
        string FontFamily,
        Color Color,
        bool Outline,
        int FontWeight,
        double OutlineWidth,
        bool ShowTranslation,
        bool Glow,
        bool CharFloat,
        bool CharScale);
}
