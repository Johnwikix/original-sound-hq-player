using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/描边/字重/描边宽度/翻译/逐字动效开关与强度/长音节阈值），与主界面歌词设置互不影响。
    /// UseCustomColor = false（默认）时悬浮窗忽略 Color，按窗口周围环境自动取黑/白文字色（见 DesktopLyricsAdaptiveColor）。</summary>
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
        bool CharScale,
        double LongSyllableThreshold,
        double GlowAmount,
        double CharFloatAmount,
        double CharScaleAmount,
        bool UseCustomColor);
}
