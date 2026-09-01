using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/字重/翻译/逐字动效开关与强度/长音节阈值），与主界面歌词设置互不影响。
    /// 可读性由环境自适应取色保证（窗口按背景切黑/白文字色），无描边。
    /// UseCustomColor = false（默认）时悬浮窗忽略 Color，按窗口周围环境自动取黑/白文字色（见 DesktopLyricsAdaptiveColor）。</summary>
    public readonly record struct DesktopLyricsStyle(
        double FontSize,
        string FontFamily,
        Color Color,
        int FontWeight,
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
