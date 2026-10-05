using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Windows.UI;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>
/// 纯本地歌词主题引擎。它使用内置情绪词表和确定性配色规则，不访问网络，
/// 生成结果按歌词/标题哈希缓存，保证切换窗口或重复播放时不会重复计算。
/// </summary>
public sealed class LocalLyricThemeService
{
    private readonly ConcurrentDictionary<string, DesktopLyricsTheme> _cache = new(StringComparer.Ordinal);
    private static readonly string[] ConceptNames = ["浮名", "流光", "心象", "潮汐", "余温", "星痕", "回声", "长昼"];

    private static readonly EmotionPalette[] Palettes =
    [
        new("静夜", ["安静", "寂静", "月", "海", "风", "睡", "梦", "夜", "远方"],
            Color.FromArgb(0xFF, 0x09, 0x13, 0x2A), Color.FromArgb(0xFF, 0x1C, 0x3D, 0x66),
            Color.FromArgb(0xFF, 0xF2, 0xF7, 0xFF), Color.FromArgb(0xFF, 0xA9, 0xC7, 0xE8),
            Color.FromArgb(0xFF, 0x74, 0xB9, 0xFF)),
        new("余烬", ["火", "燃", "热烈", "心跳", "舞", "奔跑", "自由", "疯狂", "醒来"],
            Color.FromArgb(0xFF, 0x2A, 0x0D, 0x12), Color.FromArgb(0xFF, 0x67, 0x21, 0x2A),
            Color.FromArgb(0xFF, 0xFF, 0xF3, 0xE6), Color.FromArgb(0xFF, 0xFF, 0xB0, 0x8A),
            Color.FromArgb(0xFF, 0xFF, 0x64, 0x82)),
        new("微光", ["希望", "明天", "黎明", "星", "花", "重生", "拥抱", "光", "温柔"],
            Color.FromArgb(0xFF, 0x0A, 0x1F, 0x1B), Color.FromArgb(0xFF, 0x1E, 0x56, 0x4A),
            Color.FromArgb(0xFF, 0xEC, 0xFF, 0xF8), Color.FromArgb(0xFF, 0xA9, 0xE8, 0xCC),
            Color.FromArgb(0xFF, 0x62, 0xD5, 0xB0)),
        new("雨痕", ["孤独", "寂寞", "雨", "泪", "离开", "再见", "失去", "遗憾", "空"],
            Color.FromArgb(0xFF, 0x16, 0x10, 0x2A), Color.FromArgb(0xFF, 0x3E, 0x2D, 0x64),
            Color.FromArgb(0xFF, 0xF5, 0xED, 0xFF), Color.FromArgb(0xFF, 0xCA, 0xB7, 0xEF),
            Color.FromArgb(0xFF, 0xAE, 0x8C, 0xFF)),
        new("暖潮", ["爱", "喜欢", "甜", "暖", "相遇", "陪伴", "想念", "依靠", "亲吻"],
            Color.FromArgb(0xFF, 0x2B, 0x16, 0x18), Color.FromArgb(0xFF, 0x6B, 0x3C, 0x3C),
            Color.FromArgb(0xFF, 0xFF, 0xF2, 0xE9), Color.FromArgb(0xFF, 0xFF, 0xC0, 0xA5),
            Color.FromArgb(0xFF, 0xFF, 0x8E, 0x74))
    ];

    public DesktopLyricsTheme GetTheme(IList<LyricLine>? lyrics, Music? music)
    {
        string source = BuildSource(lyrics, music);
        string key = ComputeKey(source);
        return _cache.GetOrAdd(key, _ => BuildTheme(source));
    }

    public void Clear() => _cache.Clear();

    private static string BuildSource(IList<LyricLine>? lyrics, Music? music)
    {
        var builder = new StringBuilder();
        if (lyrics is not null)
        {
            foreach (var line in lyrics)
            {
                foreach (var word in line.Words)
                    builder.Append(word.Word);
                builder.Append('|');
            }
        }

        builder.Append('|').Append(music?.Title).Append('|').Append(music?.Author);

        return builder.ToString().Trim();
    }

    private static DesktopLyricsTheme BuildTheme(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return DesktopLyricsTheme.Default;

        var scores = new int[Palettes.Length];
        for (int i = 0; i < Palettes.Length; i++)
        {
            foreach (string keyword in Palettes[i].Keywords)
                scores[i] += CountOccurrences(source, keyword);
        }

        int selected = 0;
        for (int i = 1; i < scores.Length; i++)
        {
            if (scores[i] > scores[selected])
                selected = i;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        if (scores[selected] == 0)
            selected = hash[0] % Palettes.Length;

        EmotionPalette palette = Palettes[selected];
        int variation = source.Length % 7;
        Color background = Shift(palette.Background, variation * 2 - 6, variation - 3, variation * 2 - 6);
        Color secondaryBackground = Mix(palette.SecondaryBackground, palette.Background, 0.25);
        string name = $"{ConceptNames[hash[2] % ConceptNames.Length]}·{palette.Name}";
        var wordColors = new Dictionary<string, Color>(StringComparer.Ordinal);
        int colorIndex = 0;
        foreach (string keyword in palette.Keywords)
        {
            if (source.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                wordColors[keyword] = colorIndex++ % 2 == 0
                    ? palette.Accent
                    : Mix(palette.Accent, palette.Primary, 0.32);
            }
        }
        foreach (string concept in ConceptNames)
        {
            if (source.Contains(concept, StringComparison.OrdinalIgnoreCase))
                wordColors[concept] = palette.Accent;
        }

        return new DesktopLyricsTheme(
            name,
            $"根据歌词情绪在本机生成的{palette.Name}主题",
            background,
            secondaryBackground,
            palette.Primary,
            palette.Secondary,
            palette.Accent)
        {
            WordColors = wordColors,
            VisualMode = DesktopLyricsVisualMode.Fume
        };
    }

    private static int CountOccurrences(string source, string keyword)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(keyword, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            offset += keyword.Length;
        }
        return count;
    }

    private static string ComputeKey(string source)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));

    private static Color Mix(Color first, Color second, double secondWeight)
    {
        secondWeight = Math.Clamp(secondWeight, 0, 1);
        double firstWeight = 1 - secondWeight;
        return Color.FromArgb(
            0xFF,
            (byte)Math.Clamp(Math.Round(first.R * firstWeight + second.R * secondWeight), 0, 255),
            (byte)Math.Clamp(Math.Round(first.G * firstWeight + second.G * secondWeight), 0, 255),
            (byte)Math.Clamp(Math.Round(first.B * firstWeight + second.B * secondWeight), 0, 255));
    }

    private static Color Shift(Color color, int red, int green, int blue)
        => Color.FromArgb(0xFF,
            (byte)Math.Clamp(color.R + red, 0, 255),
            (byte)Math.Clamp(color.G + green, 0, 255),
            (byte)Math.Clamp(color.B + blue, 0, 255));

    private sealed record EmotionPalette(
        string Name,
        string[] Keywords,
        Color Background,
        Color SecondaryBackground,
        Color Primary,
        Color Secondary,
        Color Accent);
}
