using System.Collections.Immutable;

namespace WinUIMusicPlayer.Model;

public enum LyricsFormat { Unknown, Lrc, EnhancedLrc, Krc, Qrc, Ttml }

public sealed record LyricsText(string Content, LyricsFormat Format);

/// <summary>A single original and optional line-timed translation. No UI or provider state.</summary>
public sealed record LyricsDocument(LyricsText Original, string? TranslationLrc)
{
    public static LyricsDocument Empty { get; } = new(new("", LyricsFormat.Unknown), null);
}

public sealed record ParsedLyricWord(string Text, double StartMs, double EndMs);
public sealed record ParsedLyricLine(string Text, double StartMs, double EndMs,
    ImmutableArray<ParsedLyricWord> Words, string Translation);

public sealed record LyricsSnapshot(LyricsDocument Document, long Revision, string SourceKind,
    string SourceKey, string Diagnostic = "");
