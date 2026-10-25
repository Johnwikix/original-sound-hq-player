using System.Collections.Immutable;

namespace WinUIMusicPlayer.Model;

public enum LyricsFormat { Unknown, Lrc, EnhancedLrc, Krc, Qrc, Ttml }

public sealed record LyricsText(string Content, LyricsFormat Format);

/// <summary>An original lyric and optional line-timed companion tracks. No UI or provider state.</summary>
public sealed record LyricsDocument(LyricsText Original, string? TranslationLrc, string? PronunciationLrc = null)
{
    public static LyricsDocument Empty { get; } = new(new("", LyricsFormat.Unknown), null, null);
}

public sealed record ParsedLyricWord(string Text, double StartMs, double EndMs);
public sealed record ParsedLyricLine(string Text, double StartMs, double EndMs,
    ImmutableArray<ParsedLyricWord> Words, string Translation, string Pronunciation = "");

public sealed record LyricsSnapshot(LyricsDocument Document, long Revision, string SourceKind,
    string SourceKey, string Diagnostic = "");
