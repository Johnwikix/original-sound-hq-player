using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

public static class LyricsFilePolicy
{
    public const string DefaultOrder = "krc,qrc,lrc,ttml";
    private static readonly string[] Formats = ["krc", "qrc", "lrc", "ttml"];

    public static string NormalizeOrder(string? order)
    {
        var values = new List<string>(4);
        foreach (string item in (order ?? "").Split(','))
        {
            string value = item.Trim().TrimStart('.').ToLowerInvariant();
            if (Array.IndexOf(Formats, value) >= 0 && !values.Contains(value)) values.Add(value);
        }
        foreach (string value in Formats) if (!values.Contains(value)) values.Add(value);
        return string.Join(',', values);
    }

    public static string[] Extensions(string? order) => NormalizeOrder(order).Split(',');
    public static string Extension(LyricsFormat format) => format switch
    {
        LyricsFormat.Ttml => ".ttml", LyricsFormat.Krc => ".krc", LyricsFormat.Qrc => ".qrc", _ => ".lrc"
    };

    public static IEnumerable<string> TranslationNames(string stem, string mainExtension, string order)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ext in new[] { "lrc", mainExtension.TrimStart('.') }.Concat(Extensions(order)))
            if (seen.Add(ext)) yield return stem + "_Translated." + ext;
    }

    public static IEnumerable<string> PronunciationNames(string stem, string mainExtension, string order)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string ext in new[] { "lrc", mainExtension.TrimStart('.'), "krc", "qrc", "ttml" }.Concat(Extensions(order)))
            if (seen.Add(ext)) yield return stem + "_Pronunciation." + ext;
    }

    public static async Task<string> ReadAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > LyricsParser.MaxContentLength) throw new FormatException("Lyrics exceed the file size limit.");
        byte[] data = new byte[stream.Length];
        await stream.ReadExactlyAsync(data, token);
        return ReadText(data);
    }

    public static string ReadText(byte[] data)
    {
        if (data.Length > LyricsParser.MaxContentLength) throw new FormatException("Lyrics exceed the file size limit.");
        using var bytes = new MemoryStream(data, false);
        using var reader = new StreamReader(bytes, Encoding.UTF8, true);
        string content = reader.ReadToEnd();
        // QRC text may be wrapped in XML; unwrap before syntax detection.
        if (content.TrimStart().StartsWith("<?xml", StringComparison.Ordinal) || content.TrimStart().StartsWith("<Qrc", StringComparison.OrdinalIgnoreCase))
        {
            using var xmlReader = System.Xml.XmlReader.Create(new StringReader(content), new System.Xml.XmlReaderSettings
            { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = LyricsParser.MaxContentLength });
            var xml = System.Xml.Linq.XDocument.Load(xmlReader);
            var value = xml.Descendants().Attributes("LyricContent").FirstOrDefault();
            if (value is not null) return value.Value;
        }
        return content;
    }
}
