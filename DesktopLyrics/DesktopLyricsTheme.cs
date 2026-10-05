using System;
using System.Collections.Generic;
using Windows.UI;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// 本地歌词主题。主题只包含可直接绘制的值，不保存任何远程模型或端点信息。
/// </summary>
public readonly record struct DesktopLyricsTheme(
    string Name,
    string Description,
    Color Background,
    Color SecondaryBackground,
    Color Primary,
    Color Secondary,
    Color Accent)
{
    /// <summary>从歌词中命中的词到强调色的本地映射，供逐词动画使用。</summary>
    public IReadOnlyDictionary<string, Color> WordColors { get; init; } =
        new Dictionary<string, Color>(StringComparer.Ordinal);

    public DesktopLyricsVisualMode VisualMode { get; init; } = DesktopLyricsVisualMode.Fume;

    public static DesktopLyricsTheme Default => new DesktopLyricsTheme(
        "本地·静夜",
        "没有歌词时使用的本地暗色主题",
        Color.FromArgb(0xFF, 0x0B, 0x12, 0x25),
        Color.FromArgb(0xFF, 0x18, 0x27, 0x4A),
        Color.FromArgb(0xFF, 0xF4, 0xF8, 0xFF),
        Color.FromArgb(0xFF, 0xB7, 0xC9, 0xE8),
        Color.FromArgb(0xFF, 0x75, 0xB9, 0xFF)) with
    { VisualMode = DesktopLyricsVisualMode.Fume };
}
