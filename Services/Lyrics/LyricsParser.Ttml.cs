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

    private (string Original, string? Translation) SplitTtml(string content, string? language, CancellationToken token, bool extractTranslation)
    {
        var xml = TryReadXml(content) ?? throw new FormatException("Invalid TTML.");
        var lines = ParseSource(content, Model.LyricsFormat.Ttml, token);
        var translations = xml.Descendants(Itunes + "translation")
            .Where(node => (string?)node.Attribute("type") == "subtitle").ToList();
        var selected = translations.FirstOrDefault(node => string.Equals((string?)node.Attribute(XNamespace.Xml + "lang"), language, StringComparison.OrdinalIgnoreCase))
            ?? translations.FirstOrDefault(node => language is not null && ((string?)node.Attribute(XNamespace.Xml + "lang"))?.Split('-')[0] == language.Split('-')[0])
            ?? translations.FirstOrDefault();
        string? translation = null;
        if (extractTranslation && selected is not null)
        {
            var unique = lines.Where(line => line.Key is not null).GroupBy(line => line.Key!).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => group.First());
            var starts = lines.GroupBy(line => line.Start).ToDictionary(group => group.Key, group => group.Count());
            var mapped = new List<(double, string)>();
            foreach (var text in selected.Elements(Itunes + "text"))
            {
                token.ThrowIfCancellationRequested();
                if (unique.TryGetValue((string?)text.Attribute("for") ?? "", out var line))
                {
                    if (starts[line.Start] != 1) throw new FormatException("TTML translation has ambiguous line timestamps.");
                    mapped.Add((line.Start, text.Value));
                }
            }
            translation = GenerateLrc(mapped);
        }
        // Only detach known auxiliary metadata; original timing, whitespace and keys stay intact.
        xml.Descendants(Itunes + "translations").Remove();
        xml.Descendants(Itunes + "transliterations").Remove();
        return (xml.ToString(SaveOptions.DisableFormatting), translation);
    }

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
            result.Add(new(lineText, start.Value, end, words.ToImmutable(), (string?)p.Attribute(Itunes + "key")));
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
            string? role = (string?)child.Attribute(Metadata + "role");
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
