using System;
using System.Collections.Generic;
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
            string? rootLanguage = (string?)xml.Root?.Attribute(xmlNamespace + "lang");
            if (!string.IsNullOrWhiteSpace(rootLanguage)) return Parse(rootLanguage);

            foreach (XElement node in xml.Descendants())
            {
                if (IsAuxiliaryNode(node)) continue;
                string? language = (string?)node.Attribute(xmlNamespace + "lang");
                if (!string.IsNullOrWhiteSpace(language)) return Parse(language);
            }
            return null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Uses an explicit source tag first, then the writing system in the lyric text.
    /// Han-only text defaults to Mandarin because script alone cannot prove Cantonese.
    /// </summary>
    public static LyricsLanguage? Detect(LyricsDocument document)
        => DetectExplicit(document) ?? DetectScript(document.Original.Content);

    public static LyricsLanguage? DetectLines(IEnumerable<string> lines)
        => DetectScript(string.Join(string.Empty, lines));

    public static LyricsLanguage? DetectScript(string text)
    {
        text = ExtractSourceText(text);
        bool hasHan = false;
        bool hasKana = false;
        bool hasHangul = false;
        foreach (char c in text)
        {
            if (IsKana(c)) hasKana = true;
            else if (IsHangul(c)) hasHangul = true;
            else if (IsHan(c)) hasHan = true;
        }

        // Kana is a reliable Japanese signal, including Japanese lines containing kanji.
        if (hasKana) return LyricsLanguage.Japanese;
        if (hasHangul) return LyricsLanguage.Korean;
        if (hasHan) return LyricsLanguage.Mandarin;
        return null;
    }

    private static string ExtractSourceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("<", StringComparison.Ordinal)) return text;
        try
        {
            var xml = XDocument.Parse(text, LoadOptions.PreserveWhitespace);
            var source = xml.Descendants()
                .Where(node => !IsAuxiliaryNode(node))
                .Select(node => node.Name.LocalName.Equals("text", StringComparison.OrdinalIgnoreCase)
                    ? node.Value : node.Nodes().OfType<XText>().FirstOrDefault()?.Value ?? string.Empty);
            return string.Join(string.Empty, source);
        }
        catch (Exception) { return text; }
    }

    private static bool IsAuxiliaryNode(XElement node)
        => IsAuxiliaryMarker(node) || node.Ancestors().Any(IsAuxiliaryMarker);

    private static bool IsAuxiliaryMarker(XElement node)
        => node.Name.LocalName.Equals("transliteration", StringComparison.OrdinalIgnoreCase)
            || node.Name.LocalName.Equals("transliterations", StringComparison.OrdinalIgnoreCase)
            || node.Attributes().Any(attribute =>
                attribute.Name.LocalName.Equals("role", StringComparison.OrdinalIgnoreCase)
                && string.Equals(attribute.Value, "x-roman", StringComparison.OrdinalIgnoreCase));

    internal static bool IsKana(char c)
        => c is >= '\u3040' and <= '\u30ff' or >= '\u31f0' and <= '\u31ff';

    internal static bool IsHangul(char c)
        => c is >= '\u1100' and <= '\u11ff'
            or >= '\u3130' and <= '\u318f'
            or >= '\uac00' and <= '\ud7af';

    internal static bool IsHan(char c)
        => c is >= '\u3400' and <= '\u4dbf'
            or >= '\u4e00' and <= '\u9fff'
            or >= '\uf900' and <= '\ufaff';

    private static LyricsLanguage? Parse(string? language)
    {
        if (string.IsNullOrWhiteSpace(language)) return null;
        string[] parts = language.Replace('_', '-').Split('-');
        string code = parts[0].ToLowerInvariant();
        if (code == "yue") return LyricsLanguage.Cantonese;
        return code switch
        {
            // Generic zh does not distinguish Mandarin from Cantonese. Script
            // fallback therefore treats Han-only lyrics as Mandarin.
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
        => Detect(document) is { } language && IsEnabled(language);
}
