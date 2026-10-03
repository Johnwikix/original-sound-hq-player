using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace WinUIMusicPlayer.Services.Lyrics;

public sealed partial class LyricsParser
{
    private static readonly XNamespace Tt = "http://www.w3.org/ns/ttml";
    private static readonly XNamespace Itunes = "http://music.apple.com/lyric-ttml-internal";
    private static readonly XNamespace ItunesExtensions = "http://itunes.apple.com/lyric-ttml-extensions";
    private static readonly XNamespace Metadata = "http://www.w3.org/ns/ttml#metadata";

    private static XDocument? TryReadXml(string content)
    {
        try
        {
            using var input = new StringReader(content);
            using var reader = XmlReader.Create(input, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = MaxContentLength
            });
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException) { return null; }
    }

    private (string Original, string? Translation, string? Pronunciation) SplitTtml(
        string content, string? language, CancellationToken token, bool extractTranslation, bool extractPronunciation)
    {
        var xml = TryReadXml(content) ?? throw new FormatException("Invalid TTML.");
        var lines = ParseSource(content, Model.LyricsFormat.Ttml, token);
        var translations = xml.Descendants()
            .Where(node => node.Name.LocalName == "translation")
            .Where(node => (string?)node.Attribute("type") == "subtitle").ToList();
        var selected = translations.FirstOrDefault(node => string.Equals((string?)node.Attribute(XNamespace.Xml + "lang"), language, StringComparison.OrdinalIgnoreCase))
            ?? translations.FirstOrDefault(node => language is not null && ((string?)node.Attribute(XNamespace.Xml + "lang"))?.Split('-')[0] == language.Split('-')[0])
            ?? translations.FirstOrDefault();
        string? translation = null;
        string? pronunciation = null;
        if (extractTranslation && selected is not null)
        {
            var unique = lines.Where(line => line.Key is not null).GroupBy(line => line.Key!).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First());
            var starts = lines.GroupBy(line => line.Start).ToDictionary(group => group.Key, group => group.Count());
            var mapped = new List<(double, string)>();
            foreach (var text in selected.Elements().Where(node => node.Name.LocalName == "text"))
            {
                token.ThrowIfCancellationRequested();
                var key = GetTtmlKey(text);
                if (key is not null && unique.TryGetValue(key, out var line))
                {
                    if (starts[line.Start] != 1) throw new FormatException("TTML translation has ambiguous line timestamps.");
                    mapped.Add((line.Start, text.Value));
                }
            }
            translation = GenerateLrc(mapped);
        }
        if (extractPronunciation)
        {
            // Transliteration is an independent track. It must still be imported when
            // the caller supplied an external translation, otherwise the pronunciation
            // silently disappears from a perfectly valid TTML document.
            var transliterations = xml.Descendants()
                .Where(node => node.Name.LocalName == "transliteration")
                .ToList();
            string? sourceLanguage = GetTtmlSourceLanguage(xml);
            string? sourceText = lines.Count == 0 ? null : string.Join("", lines.Select(line => line.Text));
            var selectedTransliteration = transliterations
                .Where(node => IsPronunciationCompatible((string?)node.Attribute(XNamespace.Xml + "lang"), sourceLanguage, sourceText))
                .OrderByDescending(node => IsSameLanguageFamily((string?)node.Attribute(XNamespace.Xml + "lang"), sourceLanguage))
                .ThenByDescending(node => IsPreferredPronunciationLanguage((string?)node.Attribute(XNamespace.Xml + "lang")))
                .FirstOrDefault();
            if (selectedTransliteration is not null)
            {
                var unique = lines.Where(line => line.Key is not null).GroupBy(line => line.Key!).Where(group => group.Count() == 1)
                    .ToDictionary(group => group.Key, group => group.First());
                var starts = lines.GroupBy(line => line.Start).ToDictionary(group => group.Key, group => group.Count());
                var mapped = new List<(double, string)>();
                foreach (var text in selectedTransliteration.Elements().Where(node => node.Name.LocalName == "text"))
                {
                    token.ThrowIfCancellationRequested();
                    var key = GetTtmlKey(text);
                    if (key is not null && unique.TryGetValue(key, out var line))
                    {
                        if (starts[line.Start] != 1) throw new FormatException("TTML pronunciation has ambiguous line timestamps.");
                        mapped.Add((line.Start, text.Value));
                    }
                }
                pronunciation = GenerateLrc(mapped);
            }
            if (string.IsNullOrWhiteSpace(pronunciation))
                pronunciation = GenerateLrc(ExtractInlinePronunciation(xml, lines, sourceLanguage, sourceText, token));
        }
        // Only detach known auxiliary metadata; original timing, whitespace and keys stay intact.
        xml.Descendants(Itunes + "translations").Remove();
        xml.Descendants(Itunes + "transliterations").Remove();
        xml.Descendants(ItunesExtensions + "translations").Remove();
        xml.Descendants(ItunesExtensions + "transliterations").Remove();
        return (xml.ToString(SaveOptions.DisableFormatting), translation, pronunciation);
    }

    private static string? GetTtmlKey(XElement element)
        => (string?)element.Attribute(Itunes + "key")
            ?? (string?)element.Attribute(ItunesExtensions + "key")
            ?? (string?)element.Attribute("key")
            ?? (string?)element.Attribute("for");

    private static bool IsTtmlRole(XElement element, string role)
        => string.Equals((string?)element.Attribute(Metadata + "role")
            ?? (string?)element.Attribute("role"), role, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(double Start, string Text)> ExtractInlinePronunciation(
        XDocument xml, IReadOnlyList<SourceLine> lines, string? sourceLanguage, string? sourceText, CancellationToken token)
    {
        var unique = lines.Where(line => line.Key is not null).GroupBy(line => line.Key!).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First());
        var starts = lines.GroupBy(line => line.Start).ToDictionary(group => group.Key, group => group.Count());
        var tracks = new Dictionary<string, (string? Language, List<(double Start, string Text)> Lines)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in xml.Descendants(Tt + "p"))
        {
            token.ThrowIfCancellationRequested();
            SourceLine? line = null;
            var key = GetTtmlKey(p);
            if (key is not null && unique.TryGetValue(key, out var keyedLine))
                line = keyedLine;
            else if (XmlTime((string?)p.Attribute("begin")) is { } start && starts.GetValueOrDefault(start) == 1)
                line = lines.FirstOrDefault(candidate => candidate.Start == start);
            if (line is null) continue;

            foreach (var roman in p.Descendants().Where(node => IsTtmlRole(node, "x-roman")
                         && !node.Ancestors().Any(ancestor => IsTtmlRole(ancestor, "x-roman"))))
            {
                string text = roman.Value;
                if (string.IsNullOrWhiteSpace(text)) continue;
                string? language = (string?)roman.Attribute(XNamespace.Xml + "lang")
                    ?? (string?)p.Attribute(XNamespace.Xml + "lang");
                string trackKey = language ?? "";
                if (!tracks.TryGetValue(trackKey, out var track))
                    tracks[trackKey] = track = (language, []);
                track.Lines.Add((line.Start, text));
            }
        }

        var selectedTrack = tracks.Values
            .Where(track => IsPronunciationCompatible(track.Language, sourceLanguage, sourceText))
            .OrderByDescending(track => IsSameLanguageFamily(track.Language, sourceLanguage))
            .ThenByDescending(track => IsPreferredPronunciationLanguage(track.Language))
            .FirstOrDefault();
        if (selectedTrack.Lines is null) return [];
        return selectedTrack.Lines.GroupBy(line => line.Start)
            .Select(group => (group.Key, string.Concat(group.Select(item => item.Text))));
    }

    private static bool IsPreferredPronunciationLanguage(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return false;
        return language.Equals("ja-Latn", StringComparison.OrdinalIgnoreCase)
            || language.Equals("ko-Latn", StringComparison.OrdinalIgnoreCase)
            || language.Equals("cmn-Latn", StringComparison.OrdinalIgnoreCase)
            || language.Equals("yue-Latn", StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetTtmlSourceLanguage(XDocument xml)
    {
        XNamespace xmlNamespace = XNamespace.Xml;
        return (string?)xml.Root?.Attribute(xmlNamespace + "lang")
            ?? (string?)xml.Root?.Element(Tt + "body")?.Attribute(xmlNamespace + "lang")
            ?? xml.Descendants(Tt + "p")
                .Select(node => (string?)node.Attribute(xmlNamespace + "lang"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static bool IsPronunciationCompatible(string? pronunciationLanguage, string? sourceLanguage, string? sourceText)
    {
        if (string.IsNullOrWhiteSpace(pronunciationLanguage)) return true;
        string pronunciationFamily = LanguageFamily(pronunciationLanguage);
        string sourceFamily = LanguageFamily(sourceLanguage);
        if (!string.IsNullOrEmpty(sourceFamily))
            return pronunciationFamily == sourceFamily;

        if (!string.IsNullOrEmpty(sourceText))
        {
            if (sourceText.Any(IsHiraganaOrKatakana)) return pronunciationFamily == "ja";
            if (sourceText.Any(IsHangul)) return pronunciationFamily == "ko";
        }
        return true;
    }

    private static bool IsSameLanguageFamily(string? pronunciationLanguage, string? sourceLanguage)
        => !string.IsNullOrWhiteSpace(sourceLanguage)
            && LanguageFamily(pronunciationLanguage) == LanguageFamily(sourceLanguage);

    private static string LanguageFamily(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return "";
        string[] parts = language.Replace('_', '-').Split('-');
        string baseLanguage = parts[0].Trim().ToLowerInvariant();
        if (baseLanguage == "yue"
            || (baseLanguage == "zh" && parts.Any(part => part.Equals("HK", StringComparison.OrdinalIgnoreCase)
                || part.Equals("MO", StringComparison.OrdinalIgnoreCase))))
            return "yue";
        return baseLanguage switch
        {
            "zh" => "cmn",
            "cmn" => "cmn",
            "ja" => "ja",
            "ko" => "ko",
            "yue" => "yue",
            _ => baseLanguage,
        };
    }

    private static bool IsHiraganaOrKatakana(char value)
        => value is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff';

    private static bool IsHangul(char value)
        => value is >= '\u1100' and <= '\u11ff' or >= '\uac00' and <= '\ud7af';

    private static List<SourceLine> ParseTtml(string content, CancellationToken token)
    {
        var xml = TryReadXml(content) ?? throw new FormatException("Invalid TTML.");
        if (xml.Root?.Name != Tt + "tt") throw new FormatException("Unsupported TTML namespace.");
        var result = new List<SourceLine>();
        var body = xml.Root.Element(Tt + "body");
        if (body is null) return result;
        foreach (var p in body.Descendants(Tt + "p"))
        {
            token.ThrowIfCancellationRequested();
            double? start = XmlTime((string?)p.Attribute("begin"));
            double? end = XmlTime((string?)p.Attribute("end"));
            var words = ImmutableArray.CreateBuilder<Model.ParsedLyricWord>();
            var text = new System.Text.StringBuilder();
            Collect(p, start, end, words, text, token);
            string lineText = text.ToString();
            if (string.IsNullOrWhiteSpace(lineText)) continue;
            start ??= words.Count > 0 ? words[0].StartMs : null;
            if (start is null) continue;
            end ??= words.Count > 0 ? words[^1].EndMs : null;
            // Untimed text around timed spans must remain visible. Fall back to line timing
            // when the word stream cannot faithfully represent all body text.
            if (string.Concat(words.Select(word => word.Text)) != lineText) words.Clear();
            result.Add(new(lineText, start.Value, end, words.ToImmutable(), GetTtmlKey(p)));
        }
        return result;
    }

    private static void Collect(XElement element, double? inheritedStart, double? inheritedEnd,
        ImmutableArray<Model.ParsedLyricWord>.Builder words, System.Text.StringBuilder text, CancellationToken token)
    {
        foreach (var node in element.Nodes())
        {
            token.ThrowIfCancellationRequested();
            if (node is XText value)
            {
                text.Append(value.Value);
                if (words.Count > 0 && string.IsNullOrWhiteSpace(value.Value))
                    words[^1] = words[^1] with { Text = words[^1].Text + value.Value };
                else if (inheritedStart is { } start && element.Name == Tt + "span")
                    words.Add(new(value.Value, start, inheritedEnd ?? start));
                continue;
            }
            if (node is not XElement child) continue;
            string? role = (string?)child.Attribute(Metadata + "role") ?? (string?)child.Attribute("role");
            if (role is "x-translation" or "x-roman" or "x-bg") continue;
            if (child.Name == Tt + "br") { text.Append(' '); continue; }
            Collect(child, XmlTime((string?)child.Attribute("begin")) ?? inheritedStart,
                XmlTime((string?)child.Attribute("end")) ?? inheritedEnd, words, text, token);
        }
    }

    private static double? XmlTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        double scale = 1000;
        if (value.EndsWith("ms", StringComparison.Ordinal)) { value = value[..^2]; scale = 1; }
        else if (value.EndsWith('s')) value = value[..^1];
        string[] parts = value.Split(':');
        if (parts.Length > 3) throw new FormatException("Unsupported TTML time expression.");
        double total = 0;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double number) || number < 0)
                throw new FormatException("Unsupported TTML time expression.");
            total = total * 60 + number;
        }
        if (total * scale > 604800000) throw new FormatException("TTML time exceeds the supported range.");
        return Math.Round(total * scale);
    }
}
