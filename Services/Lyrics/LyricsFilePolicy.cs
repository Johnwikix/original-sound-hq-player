using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    public static async Task<string> ReadAsync(string file, CancellationToken token)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        if (stream.Length > LyricsParser.MaxContentLength) throw new FormatException("Lyrics exceed the file size limit.");
        byte[] data = new byte[stream.Length];
        await stream.ReadExactlyAsync(data, token);
        return Decode(data);
    }

    public static string Decode(byte[] data)
    {
        if (data.Length > LyricsParser.MaxContentLength) throw new FormatException("Lyrics exceed the file size limit.");
        if (data.AsSpan().StartsWith("krc1"u8))
        {
            ReadOnlySpan<byte> key = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
            byte[] payload = data[4..];
            for (int i = 0; i < payload.Length; i++) payload[i] ^= key[i % key.Length];
            return Inflate(payload);
        }
        using var bytes = new MemoryStream(data, false);
        using var reader = new StreamReader(bytes, Encoding.UTF8, true);
        string content = reader.ReadToEnd();
        string hex = content.Trim();
        if (hex.Length >= 16 && hex.Length % 16 == 0 && hex.All(char.IsAsciiHexDigit))
        {
            byte[] encrypted = Convert.FromHexString(hex);
            var schedule = new byte[3][][];
            for (int i = 0; i < 3; i++)
            {
                schedule[i] = new byte[16][];
                for (int j = 0; j < 16; j++) schedule[i][j] = new byte[6];
            }
            Lyricify.Lyrics.Decrypter.Qrc.DESHelper.TripleDESKeySetup(Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL"),
                schedule, Lyricify.Lyrics.Decrypter.Qrc.DESHelper.DECRYPT);
            byte[] decrypted = new byte[encrypted.Length];
            byte[] block = new byte[8];
            for (int offset = 0; offset < encrypted.Length; offset += 8)
            {
                Lyricify.Lyrics.Decrypter.Qrc.DESHelper.TripleDESCrypt(encrypted.AsSpan(offset, 8).ToArray(), block, schedule);
                block.CopyTo(decrypted, offset);
            }
            content = Inflate(decrypted);
        }
        // QQ commonly wraps decrypted QRC in XML; unwrap before syntax detection.
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
    private static string Inflate(byte[] payload)
    {
        using var input = new MemoryStream(payload);
        using var zip = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        byte[] buffer = new byte[4096];
        int read;
        while ((read = zip.Read(buffer)) > 0)
        {
            if (output.Length + read > LyricsParser.MaxContentLength) throw new FormatException("Decompressed lyrics exceed the size limit.");
            output.Write(buffer, 0, read);
        }
        return Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length).TrimStart('\uFEFF');
    }

}
