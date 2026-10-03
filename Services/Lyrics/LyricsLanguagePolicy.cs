using System;
using System.Linq;
using System.Xml.Linq;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

public enum LyricsLanguage
{
    Other,
    Mandarin,
    Cantonese,
    Japanese,
    Korean,
}

/// <summary>Detects the source language used to decide which pronunciation track is allowed.</summary>
public static class LyricsLanguagePolicy
{
    /// <summary>Returns a language only when the lyric format carries an explicit source tag.</summary>
    public static LyricsLanguage? DetectExplicit(LyricsDocument document)
    {
        if (document.Original.Format != LyricsFormat.Ttml) return null;
        try
        {
            var xml = XDocument.Parse(document.Original.Content, LoadOptions.PreserveWhitespace);
            XNamespace xmlNamespace = XNamespace.Xml;
            string? language = (string?)xml.Root?.Attribute(xmlNamespace + "lang")
                ?? (string?)xml.Root?.Elements().FirstOrDefault()?.Attribute(xmlNamespace + "lang")
                ?? xml.Descendants().Select(node => (string?)node.Attribute(xmlNamespace + "lang"))
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            return Parse(language);
        }
        catch (Exception) { return null; }
    }

    private static LyricsLanguage? Parse(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string[] parts = language.Replace('_', '-').Split('-');
        string code = parts[0].ToLowerInvariant();
        if (code == "yue") return LyricsLanguage.Cantonese;
        return code switch
        {
            // Generic zh does not distinguish Mandarin from Cantonese; leave it
            // unclassified until a provider supplies a more specific tag.
            "cmn" => LyricsLanguage.Mandarin,
            "ja" => LyricsLanguage.Japanese,
            "ko" => LyricsLanguage.Korean,
            _ => null,
        };
    }

    public static bool IsEnabled(LyricsLanguage language)
        => language switch
        {
            LyricsLanguage.Mandarin => Model.AppSettings.IsMandarinPronunciationEnabled,
            LyricsLanguage.Cantonese => Model.AppSettings.IsCantonesePronunciationEnabled,
            LyricsLanguage.Japanese => Model.AppSettings.IsJapanesePronunciationEnabled,
            LyricsLanguage.Korean => Model.AppSettings.IsKoreanPronunciationEnabled,
            _ => false,
        };

    public static bool IsEnabledForDocument(LyricsDocument document)
        => DetectExplicit(document) is not { } language || IsEnabled(language);
}
