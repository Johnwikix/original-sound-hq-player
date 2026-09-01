using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/字重/翻译/逐字动效开关与强度/长音节阈值），与主界面歌词设置互不影响。
    /// 文字带可读性软阴影（颜色 = 文字色反相），环境自适应取色仍按背景切黑/白文字色（见 DesktopLyricsAdaptiveColor）。
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

    /// <summary>文字软阴影的共享参数与颜色规则，两个渲染器（Composition / Win2D）同一套观感：
    /// 阴影色 = 文字色 RGB 反相（自适应黑/白与自定义颜色都成立：白字黑影、黑字白影、红字青影），
    /// 作为混合背景上黑/白二选一必有可读性缺口时的兜底，均匀背景下近乎无感。</summary>
    internal static class DesktopLyricsShadow
    {
        /// <summary>TextBlock 渲染器（Composition DropShadow.BlurRadius）的模糊半径。</summary>
        public const double BlurRadius = 10;

        /// <summary>Win2D 渲染器（GaussianBlurEffect.BlurAmount = σ）的模糊量：
        /// 高斯 σ 与视觉"模糊半径"约 2~3 倍关系，取与上面 BlurRadius 同档观感。</summary>
        public const float BlurSigma = 4f;

        /// <summary>阴影强度（Composition ShadowOpacity / Win2D 画笔 alpha）。</summary>
        public const double Opacity = 0.75;

        /// <summary>文字色的不透明反相色；强度由各渲染器的透明度通道单独施加。</summary>
        public static Color Invert(Color textColor)
            => Color.FromArgb(255, (byte)(255 - textColor.R), (byte)(255 - textColor.G), (byte)(255 - textColor.B));
    }
}
