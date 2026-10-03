using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NMeCab.Specialized;
using Pinyin;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>Line-aligned, transient output. This type has no persistence or export path.</summary>
public sealed record RomanizeResult(LyricsLanguage Language, ImmutableArray<string> Lines,
    ImmutableArray<bool> Fallbacks);

/// <summary>
/// C# adaptation of lyric-romanizer's routing, lazy engines and per-line fallback.
/// Japanese/Chinese use local .NET dictionaries; Korean ports @romanize/korean's RR rules.
/// The About page lists the bundled licenses and the local engine dependencies.
/// </summary>
public sealed class LyricsRomanizer : IDisposable
{
    private readonly object _gate = new();
    private readonly string _japaneseDictionaryPath;
    private readonly IReadOnlyDictionary<LyricsLanguage, Func<string, string>>? _engines;
    private MeCabIpaDicTagger? _japaneseTagger;
    private bool _disposed;

    public LyricsRomanizer() : this(Path.Combine(AppContext.BaseDirectory, "IpaDic")) { }

    public LyricsRomanizer(string japaneseDictionaryPath,
        IReadOnlyDictionary<LyricsLanguage, Func<string, string>>? engines = null)
    {
        _japaneseDictionaryPath = japaneseDictionaryPath;
        _engines = engines is null ? null : new Dictionary<LyricsLanguage, Func<string, string>>(engines);
    }

    public Task<RomanizeResult> RomanizeLinesAsync(IReadOnlyList<string> lines,
        LyricsLanguage? language = null, CancellationToken token = default)
        => Task.Run(() => RomanizeLines(lines, language, token), token);

    public RomanizeResult RomanizeLines(IReadOnlyList<string> lines, LyricsLanguage? language = null,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        LyricsLanguage selected = language ?? LyricsLanguagePolicy.DetectLines(lines) ?? LyricsLanguage.Other;
        var output = ImmutableArray.CreateBuilder<string>(lines.Count);
        var fallbacks = ImmutableArray.CreateBuilder<bool>(lines.Count);
        // Serialize dictionary access and disposal. A cancelled queued song can
        // leave promptly without waiting for another song's dictionary load.
        Enter(token);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            for (int index = 0; index < lines.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                string source = lines[index];
                string converted = source;
                bool fallback = false;
                LyricsLanguage? lineLanguage = DetectLineLanguage(source, selected);
                if (lineLanguage is { } languageForLine)
                {
                    try { converted = ConvertLine(source, languageForLine); }
                    catch (Exception ex) when (ex is not OperationCanceledException
                        and not OutOfMemoryException and not AccessViolationException)
                    {
                        // Preserve a failed line and retry on the next call.
                        fallback = true;
                    }
                    if (NeedsRomanization(converted, languageForLine)) fallback = true;
                }
                output.Add(converted);
                fallbacks.Add(fallback);
            }
            token.ThrowIfCancellationRequested();
            return new(selected, output.MoveToImmutable(), fallbacks.MoveToImmutable());
        }
        finally { Monitor.Exit(_gate); }
    }

    public string RomanizeLine(string line, LyricsLanguage? language = null, CancellationToken token = default)
        => RomanizeLines([line], language, token).Lines[0];

    public string? GeneratePronunciationLrc(LyricsDocument document, double durationMs, LyricsParser parser,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(parser);
        if (!parser.HasLyrics(document, token)) return null;
        LyricsLanguage? language = LyricsLanguagePolicy.Detect(document);
        if (language is null or LyricsLanguage.Other) return null;
        var parsed = parser.Parse(document, durationMs, token);
        var result = RomanizeLines(parsed.Select(line => line.Text).ToArray(), language, token);
        var builder = new StringBuilder();
        for (int index = 0; index < parsed.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            // A pinned song language does not make every line belong to that
            // script. Keep English, numbers and punctuation out of the
            // pronunciation track instead of duplicating them above the lyric.
            if (DetectLineLanguage(parsed[index].Text, language.Value) is not { } lineLanguage
                || !LyricsLanguagePolicy.IsEnabled(lineLanguage)
                || result.Fallbacks[index] || string.IsNullOrWhiteSpace(result.Lines[index])) continue;
            int total = Math.Max(0, (int)Math.Round(parsed[index].StartMs, MidpointRounding.AwayFromZero));
            builder.Append('[').Append(string.Create(CultureInfo.InvariantCulture,
                $"{total / 60000:00}:{total / 1000 % 60:00}.{total % 1000:000}"))
                .Append(']').Append(result.Lines[index]).AppendLine();
        }
        return builder.Length == 0 ? null : builder.ToString();
    }

    public Task WarmupAsync(LyricsLanguage language, CancellationToken token = default)
        => Task.Run(() =>
        {
            Enter(token);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_engines?.ContainsKey(language) == true) return;
                switch (language)
                {
                    case LyricsLanguage.Mandarin: _ = Pinyin.Pinyin.Instance; break;
                    case LyricsLanguage.Cantonese: _ = Jyutping.Instance; break;
                    case LyricsLanguage.Japanese: _ = GetJapaneseTagger(); break;
                }
                token.ThrowIfCancellationRequested();
            }
            finally { Monitor.Exit(_gate); }
        }, token);

    private void Enter(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        while (!Monitor.TryEnter(_gate, 50)) token.ThrowIfCancellationRequested();
    }

    private string ConvertLine(string text, LyricsLanguage language)
    {
        if (_engines is not null && _engines.TryGetValue(language, out var engine)) return engine(text);
        return language switch
        {
            LyricsLanguage.Mandarin => RomanizeChinese(text, cantonese: false),
            LyricsLanguage.Cantonese => RomanizeChinese(text, cantonese: true),
            LyricsLanguage.Japanese => RomanizeJapanese(text),
            LyricsLanguage.Korean => KoreanRomanizer.Romanize(text),
            _ => text,
        };
    }

    private static string RomanizeChinese(string text, bool cantonese)
    {
        var result = new StringBuilder(text.Length * 2);
        for (int index = 0; index < text.Length;)
        {
            if (!LyricsLanguagePolicy.IsHan(text[index]))
            {
                result.Append(text[index++]);
                continue;
            }
            int start = index++;
            while (index < text.Length && LyricsLanguagePolicy.IsHan(text[index])) index++;
            string word = text[start..index];
            // Convert only Han runs so Latin words, punctuation and spaces survive intact.
            string reading = cantonese
                ? Jyutping.Instance.HanziToPinyin(word, CanTone.Style.TONE3, Error.Default, false).ToStr()
                : Pinyin.Pinyin.Instance.HanziToPinyin(word, ManTone.Style.TONE, Error.Default, false, true, false).ToStr();
            if (start > 0 && char.IsLetterOrDigit(text[start - 1])) result.Append(' ');
            result.Append(reading);
            if (index < text.Length && char.IsLetterOrDigit(text[index])) result.Append(' ');
        }
        return result.ToString();
    }

    private MeCabIpaDicTagger GetJapaneseTagger()
        => _japaneseTagger ??= MeCabIpaDicTagger.Create(_japaneseDictionaryPath, []);

    private string RomanizeJapanese(string text)
    {
        var result = new StringBuilder(text.Length * 2);
        int cursor = 0;
        foreach (var node in GetJapaneseTagger().Parse(text))
        {
            if (string.IsNullOrEmpty(node.Surface)) continue;
            int start = text.IndexOf(node.Surface, cursor, StringComparison.Ordinal);
            if (start < 0) throw new FormatException("Japanese token does not match its source.");
            result.Append(text, cursor, start - cursor);
            string reading = node.Reading;
            string romanized = string.IsNullOrEmpty(reading) || reading == "*"
                ? Kana.Kana.KanaToRomaji(node.Surface, Kana.Error.Default, true).ToStr("")
                : Kana.Kana.KanaToRomaji(reading, Kana.Error.Default, true).ToStr("");
            // IPA readings spell particles as written; pronounce them in context.
            if (node.PartsOfSpeech == "助詞")
                romanized = node.Surface switch { "は" => "wa", "へ" => "e", "を" => "o", _ => romanized };
            if (start == cursor && result.Length > 0 && romanized.Length > 0
                && char.IsLetterOrDigit(result[^1]) && char.IsLetterOrDigit(romanized[0]))
                result.Append(' ');
            result.Append(romanized);
            cursor = start + node.Surface.Length;
        }
        result.Append(text, cursor, text.Length - cursor);
        return result.ToString();
    }

    internal static bool NeedsRomanization(string text, LyricsLanguage language)
    {
        foreach (char c in text)
        {
            if ((language is LyricsLanguage.Mandarin or LyricsLanguage.Cantonese) && LyricsLanguagePolicy.IsHan(c)) return true;
            if (language == LyricsLanguage.Japanese && (LyricsLanguagePolicy.IsHan(c) || LyricsLanguagePolicy.IsKana(c))) return true;
            if (language == LyricsLanguage.Korean && LyricsLanguagePolicy.IsHangul(c)) return true;
        }
        return false;
    }

    /// <summary>
    /// Routes a line independently while retaining the song-level default for
    /// Han-only Japanese lines. Latin-only lines deliberately return null.
    /// </summary>
    internal static LyricsLanguage? DetectLineLanguage(string text, LyricsLanguage songLanguage)
    {
        bool hasKana = false;
        bool hasHangul = false;
        bool hasHan = false;
        foreach (char c in text)
        {
            hasKana |= LyricsLanguagePolicy.IsKana(c);
            hasHangul |= LyricsLanguagePolicy.IsHangul(c);
            hasHan |= LyricsLanguagePolicy.IsHan(c);
        }

        // A line that mixes two non-Latin writing systems cannot be safely sent
        // to one engine without segmenting it, so leave it for user-provided
        // pronunciation instead of producing a partially wrong reading.
        if (hasHangul && (hasKana || hasHan)) return null;
        if (hasKana) return LyricsLanguage.Japanese;
        if (hasHangul) return LyricsLanguage.Korean;
        if (!hasHan) return null;
        return songLanguage is LyricsLanguage.Mandarin or LyricsLanguage.Cantonese or LyricsLanguage.Japanese
            ? songLanguage
            : LyricsLanguage.Mandarin;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _japaneseTagger?.Dispose();
            _japaneseTagger = null;
        }
    }
}
