using System;
using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>桌面歌词独立样式（字号/字体/颜色/字重/翻译/逐字动效开关与强度/长音节阈值/阴影强度），与主界面歌词设置互不影响。
    /// 文字带可读性软阴影（颜色 = 文字色反相，强度由 ShadowAmount 控制，0 = 关闭），
    /// 环境自适应取色仍按背景切黑/白文字色（见 DesktopLyricsAdaptiveColor）。
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
        bool UseCustomColor,
        double ShadowAmount);

    /// <summary>文字软阴影的共享参数与颜色规则，两个渲染器（Composition / Win2D）同一套观感：
    /// 阴影色 = 文字色 RGB 反相（自适应黑/白与自定义颜色都成立：白字黑影、黑字白影、红字青影），
    /// 作为混合背景上黑/白二选一必有可读性缺口时的兜底，均匀背景下近乎无感。
    /// 强度 = 滑块百分比 / 50，线性映射到 0–2（50 = 单层满强度，100 = 双层叠加）；
    /// 因单层阴影的不透明度物理上限为 1.0，超出部分只能以第二层表达——两层透明度
    /// min(1,s) + clamp(s-1,0,1)（见 <see cref="SplitStrength"/>），曲线在 50 处连续、无档位。</summary>
    internal static class DesktopLyricsShadow
    {
        /// <summary>TextBlock 渲染器（Composition DropShadow.BlurRadius）的模糊半径。</summary>
        public const double BlurRadius = 10;

        /// <summary>Win2D 渲染器（GaussianBlurEffect.BlurAmount = σ）的模糊量：
        /// 高斯 σ 与视觉"模糊半径"约 2~3 倍关系，取与上面 BlurRadius 同档观感。</summary>
        public const float BlurSigma = 4f;

        /// <summary>把滑块百分比（0–100）换算为强度 s（0–2）并拆分到两层透明度：
        /// 内层 = min(1, s)，外层 = clamp(s-1, 0, 1)。两渲染器共用，保证强度观感一致。</summary>
        public static (float Inner, float Outer) SplitStrength(double percent)
        {
            var strength = Math.Clamp(percent, 0, 100) / 50.0;
            return ((float)Math.Min(1.0, strength), (float)Math.Clamp(strength - 1.0, 0.0, 1.0));
        }

        /// <summary>文字色的反相色，<paramref name="opacity"/>（0–1）作为阴影整体不透明度。</summary>
        public static Color Invert(Color textColor, double opacity = 1.0)
            => Color.FromArgb(
                (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)),
                (byte)(255 - textColor.R),
                (byte)(255 - textColor.G),
                (byte)(255 - textColor.B));
    }
}
