using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>Pure, bounded parsing. Published results own their immutable data; no shared object pool.</summary>
public sealed partial class LyricsParser
{
    public const int MaxContentLength = 512 * 1024;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly Regex TimeTag = new(@"[\[<](\d{1,5}:\d{2}(?:[.:]\d{1,3})?)[\]>]", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex NumericLine = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex KrcWord = new(@"<(\d+),(\d+),\d+>([^<]*)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex QrcWord = new(@"(.*?)\((\d+),(\d+)\)", RegexOptions.CultureInvariant, RegexTimeout);
    private static readonly Regex Offset = new(@"(?im)^\[offset:([+-]?\d+)\]", RegexOptions.CultureInvariant, RegexTimeout);

    private readonly object _parseLock = new();
    private readonly List<(string Content, LyricsFormat Format, List<SourceLine> Lines)> _recent = [];

    private sealed record SourceLine(string Text, double Start, double? End,
        ImmutableArray<ParsedLyricWord> Words, string? Key = null);

    public LyricsFormat Detect(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return LyricsFormat.Unknown;
        CheckSize(content);
        if (content.AsSpan().TrimStart().StartsWith("<", StringComparison.Ordinal))
            return TryReadXml(content)?.Root?.Name.LocalName == "tt" ? LyricsFormat.Ttml : LyricsFormat.Unknown;
        foreach (string raw in content.Split('\n'))
        {
            string line = raw.Trim();
            var numeric = NumericLine.Match(line);
            if (numeric.Success)
            {
                if (KrcWord.IsMatch(numeric.Groups[3].Value)) return LyricsFormat.Krc;
                if (QrcWord.IsMatch(numeric.Groups[3].Value)) return LyricsFormat.Qrc;
            }
            var tags = TimeTag.Matches(line);
            if (tags.Count == 0 || tags[0].Index != 0) continue;
            for (int i = 1; i < tags.Count; i++)
                if (tags[i].Value[0] == '<' || !string.IsNullOrWhiteSpace(line[(tags[i - 1].Index + tags[i - 1].Length)..tags[i].Index]))
                    return LyricsFormat.EnhancedLrc;
        }
        return TimeTag.IsMatch(content) ? LyricsFormat.Lrc : LyricsFormat.Unknown;
    }

    public LyricsDocument Import(string? original, string? translation = null,
        CancellationToken token = default, string? preferredLanguage = null, bool extractEmbeddedTranslation = true)
    {
        token.ThrowIfCancellationRequested();
        original ??= "";
        CheckSize(original);
        LyricsFormat format = Detect(original);
        string? embedded = null;
        if (format == LyricsFormat.Ttml)
            (original, embedded) = SplitTtml(original, preferredLanguage, token, extractEmbeddedTranslation && string.IsNullOrWhiteSpace(translation));
        else if (format == LyricsFormat.Krc && original.Contains("[language:", StringComparison.Ordinal))
        {
            var lines = ParseSource(original, format, token);
            List<string>? texts = null;
            if (extractEmbeddedTranslation && string.IsNullOrWhiteSpace(translation))
            {
                try { texts = Lyricify.Lyrics.Parsers.KrcTranslationParser.GetTranslationFromKrc(original); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { throw new FormatException("Invalid embedded KRC translation.", ex); }
            }
            if (texts is not null)
                embedded = GenerateLrc(lines.Select((line, i) => (line.Start, i < texts.Count ? texts[i] : "")));
            original = string.Join('\n', original.Split('\n').Where(line => !line.TrimStart().StartsWith("[language:", StringComparison.Ordinal)));
        }
        string? normalized = string.IsNullOrWhiteSpace(translation) ? embedded : NormalizeTranslation(translation, token);
        return new(new(original, format), string.IsNullOrWhiteSpace(normalized) ? null : normalized);
    }

    public string NormalizeTranslation(string content, CancellationToken token = default)
    {
        var lines = ParseSource(content, Detect(content), token);
        if (lines.Count == 0 && !string.IsNullOrWhiteSpace(content))
            throw new FormatException("The translation has no valid line timestamps.");
        return GenerateLrc(lines.Select(line => (line.Start, line.Text)));
    }

    public bool HasLyrics(LyricsDocument document, CancellationToken token = default)
    {
        try { return ParseSource(document.Original.Content, Detect(document.Original.Content), token).Count > 0; }
        catch (Exception ex) when (ex is FormatException or OverflowException or RegexMatchTimeoutException or System.Xml.XmlException) { return false; }
    }

    public ImmutableArray<ParsedLyricLine> Parse(LyricsDocument document, double durationMs, CancellationToken token = default)
    {
        var lines = ParseSource(document.Original.Content, Detect(document.Original.Content), token);
        // Stable ordering is essential for duplicate timestamps; duplicate rows remain original rows.
        lines = lines.OrderBy(line => line.Start).ToList();
        var translations = string.IsNullOrWhiteSpace(document.TranslationLrc)
            ? [] : ParseSource(document.TranslationLrc, LyricsFormat.Lrc, token);
        var attached = AlignTranslations(lines, translations);
        var result = ImmutableArray.CreateBuilder<ParsedLyricLine>(lines.Count);
        int nextDistinct = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var line = lines[i];
            double end = line.End ?? 0;
            if (end <= line.Start)
            {
                if (nextDistinct <= i)
                {
                    nextDistinct = i + 1;
                    while (nextDistinct < lines.Count && lines[nextDistinct].Start <= line.Start) nextDistinct++;
                }
                end = nextDistinct < lines.Count ? lines[nextDistinct].Start : Math.Max(durationMs, line.Start + 2000);
            }
            var words = line.Words;
            if (words.Length > 0 && words[^1].EndMs <= words[^1].StartMs)
                words = words.SetItem(words.Length - 1, words[^1] with { EndMs = Math.Max(end, words[^1].StartMs) });
            result.Add(new(line.Text, line.Start, end, words, attached[i] ?? ""));
        }
        return result.MoveToImmutable();
    }

    public string ExportLrc(LyricsDocument document, CancellationToken token = default) =>
        GenerateLrc(ParseSource(document.Original.Content, Detect(document.Original.Content), token).Select(line => (line.Start, line.Text)));

    private static string?[] AlignTranslations(List<SourceLine> original, List<SourceLine> translation)
    {
        var result = new string?[original.Count];
        var proposed = new Dictionary<int, List<string>>();
        foreach (var line in translation)
        {
            int low = 0, high = original.Count;
            while (low < high)
            {
                int mid = (low + high) / 2;
                if (original[mid].Start < line.Start) low = mid + 1; else high = mid;
            }
            int best = -1;
            double distance = 101;
            bool ambiguous = false;
            for (int i = Math.Max(0, low - 1); i < Math.Min(original.Count, low + 1); i++)
            {
                double delta = Math.Abs(original[i].Start - line.Start);
                if (delta < distance) { best = i; distance = delta; ambiguous = false; }
                else if (delta == distance) ambiguous = true;
            }
            if (best < 0 || ambiguous || distance > 100) continue;
            if ((best > 0 && original[best - 1].Start == original[best].Start) ||
                (best + 1 < original.Count && original[best + 1].Start == original[best].Start)) continue;
            if (!proposed.TryGetValue(best, out var texts)) proposed[best] = texts = [];
            texts.Add(line.Text);
        }
        foreach (var pair in proposed)
            if (pair.Value.Count == 1) result[pair.Key] = pair.Value[0];
        return result;
    }

    private List<SourceLine> ParseSource(string content, LyricsFormat format, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_parseLock)
            foreach (var entry in _recent)
                if (entry.Format == format && entry.Content == content) return entry.Lines;
        var lines = ParseSourceCore(content, format, token);
        lock (_parseLock)
        {
            if (_recent.Count >= 4) _recent.RemoveAt(0);
            _recent.Add((content, format, lines));
        }
        return lines;
    }

    private List<SourceLine> ParseSourceCore(string content, LyricsFormat format, CancellationToken token)
    {
        CheckSize(content);
        if (format == LyricsFormat.Ttml) return ParseTtml(content, token);
        var result = new List<SourceLine>();
        var offsetMatch = Offset.Match(content);
        double offset = 0;
        if (offsetMatch.Success && (!double.TryParse(offsetMatch.Groups[1].Value, NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out offset) || !double.IsFinite(offset) || Math.Abs(offset) > 604800000))
            throw new FormatException("Invalid lyrics offset.");
        foreach (string raw in content.Split('\n'))
        {
            token.ThrowIfCancellationRequested();
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (format is LyricsFormat.Krc or LyricsFormat.Qrc)
            {
                var match = NumericLine.Match(line);
                if (!match.Success) continue;
                double start = Number(match.Groups[1].Value) + offset;
                double end = start + Number(match.Groups[2].Value);
                var words = ImmutableArray.CreateBuilder<ParsedLyricWord>();
                foreach (Match word in (format == LyricsFormat.Krc ? KrcWord : QrcWord).Matches(match.Groups[3].Value))
                {
                    double wordStart = format == LyricsFormat.Krc ? start + Number(word.Groups[1].Value) : offset + Number(word.Groups[2].Value);
                    double wordEnd = wordStart + Number(word.Groups[format == LyricsFormat.Krc ? 2 : 3].Value);
                    string text = word.Groups[format == LyricsFormat.Krc ? 3 : 1].Value;
                    words.Add(new(text, wordStart, wordEnd));
                }
                if (words.Count > 0)
                    result.Add(new(string.Concat(words.Select(word => word.Text)), start, end, words.ToImmutable()));
                continue;
            }
            var tags = TimeTag.Matches(line);
            if (tags.Count == 0 || tags[0].Index != 0 || line[0] != '[') continue;
            bool enhanced = false;
            for (int i = 1; i < tags.Count; i++)
                enhanced |= tags[i].Value[0] == '<' || !string.IsNullOrWhiteSpace(line[(tags[i - 1].Index + tags[i - 1].Length)..tags[i].Index]);
            if (enhanced)
            {
                var words = ImmutableArray.CreateBuilder<ParsedLyricWord>();
                for (int i = 0; i < tags.Count; i++)
                {
                    int nextIndex = i + 1 < tags.Count ? tags[i + 1].Index : line.Length;
                    string text = line[(tags[i].Index + tags[i].Length)..nextIndex];
                    if (text.Length == 0) continue;
                    double start = Time(tags[i].Groups[1].Value) + offset;
                    double end = i + 1 < tags.Count ? Time(tags[i + 1].Groups[1].Value) + offset : start;
                    words.Add(new(text, start, Math.Max(start, end)));
                }
                if (words.Count > 0)
                    result.Add(new(string.Concat(words.Select(word => word.Text)), Time(tags[0].Groups[1].Value) + offset, null, words.ToImmutable()));
            }
            else
            {
                string text = line[(tags[^1].Index + tags[^1].Length)..].Trim();
                if (string.IsNullOrEmpty(text) || text == "//") continue;
                var seen = new HashSet<double>();
                foreach (Match tag in tags)
                {
                    double start = Time(tag.Groups[1].Value) + offset;
                    if (seen.Add(start)) result.Add(new(text, start, null, []));
                }
            }
        }
        return result;
    }

    private static double Number(string text)
    {
        if (!double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out double number) || number > 604800000)
            throw new FormatException("Invalid lyric time.");
        return number;
    }

    private static double Time(string value)
    {
        int colon = value.IndexOf(':');
        string seconds = value[(colon + 1)..].Replace(':', '.');
        if (!double.TryParse(seconds, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double s) || s >= 60)
            throw new FormatException("Invalid lyric timestamp.");
        return Number(value[..colon]) * 60000 + s * 1000;
    }

    private static void CheckSize(string text)
    {
        if (text.Length > MaxContentLength) throw new FormatException("Lyrics exceed the size limit.");
    }

    private static string GenerateLrc(IEnumerable<(double Start, string Text)> lines)
    {
        var output = new StringBuilder();
        foreach (var (start, text) in lines.OrderBy(line => line.Start))
        {
            if (string.IsNullOrWhiteSpace(text) || text == "//") continue;
            long ms = Math.Max(0, (long)Math.Round(start));
            output.Append(CultureInfo.InvariantCulture, $"[{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}]");
            output.Append(text.Replace("\r", "").Replace("\n", " ")).Append('\n');
        }
        return output.ToString();
    }
}
