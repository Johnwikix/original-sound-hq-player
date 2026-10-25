using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

public static class LyricsLegacyMigration
{
    public static MusicLyricsRecord Convert(MusicLyrics? legacy, LyricsParser parser, CancellationToken token = default)
    {
        var result = new MusicLyricsRecord { MusicId = legacy?.MusicId ?? 0, SourceKind = "LegacyUnknown", MigratedFromHash = Fingerprint(legacy) };
        if (legacy is null) return result;
        string original = legacy.Krc ?? "";
        string translation = legacy.TKrc ?? "";
        if (!parser.HasLyrics(new(new(original, LyricsFormat.Unknown), null), token) &&
            (parser.HasLyrics(new(new(legacy.Lyrics ?? "", LyricsFormat.Unknown), null), token) || string.IsNullOrWhiteSpace(original)))
        {
            original = legacy.Lyrics ?? "";
            translation = legacy.TranslatedLyrics ?? "";
        }
        if (string.IsNullOrWhiteSpace(original))
        {
            if (!string.IsNullOrWhiteSpace(legacy.TKrc) || !string.IsNullOrWhiteSpace(legacy.TranslatedLyrics)) result.Diagnostic = "LegacyTranslationWithoutOriginal";
            return result;
        }
        LyricsDocument document;
        try { document = parser.Import(original, translation, token); }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            result.Diagnostic = "LegacyTranslationOrFormatInvalid";
            // Preserve the original even if its translation cannot be normalized. All four source fields stay in the old table.
            try { document = parser.Import(original, null, token, extractEmbeddedTranslation: false); }
            catch (Exception error) when (error is FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { document = new(new(original, LyricsFormat.Unknown), null); }
        }
        result.Lyrics = document.Original.Content;
        result.LyricsFormat = document.Original.Format;
        result.TranslatedLyrics = document.TranslationLrc;
        if (!parser.HasLyrics(document, token)) result.Diagnostic = "LegacyOriginalUnsupported";
        return result;
    }

    public static string Fingerprint(MusicLyrics? legacy)
    {
        if (legacy is null) return "";
        // Length prefixes distinguish different field boundaries without changing stored whitespace.
        var text = new StringBuilder();
        foreach (var field in new[] { legacy.Lyrics, legacy.TranslatedLyrics, legacy.Krc, legacy.TKrc })
            text.Append(field?.Length ?? -1).Append(':').Append(field);
        return System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
