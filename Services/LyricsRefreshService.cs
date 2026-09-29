using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

/// <summary>Source resolution and display projection. The caller owns cancellation and UI publication.</summary>
public sealed class LyricsRefreshService(MusicDatabaseService database, LyricsParser parser,
    LyricsOnlineSearch online, WebDavLibraryService webDav, ILogger<LyricsRefreshService> logger)
{
    public async Task<List<LyricLine>> SetLyrics(Music music, CancellationToken token, Action<List<LyricLine>>? publishCached = null)
    {
        string order = AppSettings.LocalLyricsFormatOrder;
        LyricsSnapshot? stored = music.Id > 0 ? await database.Lyrics.GetAsync(music.Id, token).ConfigureAwait(false) : null;
        LyricsDocument? document = null;
        long externalRevision = 0;
        if (!music.IsRemote) document = await ReadLocalAsync(music.Path, order, token).ConfigureAwait(false);
        else
        {
            if (stored is not null && parser.HasLyrics(stored.Document, token))
                publishCached?.Invoke(Project(stored.Document, music.Duration.TotalMilliseconds, token));
            document = await webDav.ReadLyricsDocumentAsync(music, order, token).ConfigureAwait(false);
        }
        if (music.IsRemote && document is not null && stored is not null && stored.SourceKind != "User" &&
            document != stored.Document)
            await database.Lyrics.SaveAsync(music.Id, document, stored.Revision, "RemoteSidecar", music.Path, token).ConfigureAwait(false);
        if (document is null && music.Id <= 0 && !string.IsNullOrWhiteSpace(music.EmbeddedLyrics))
            document = TryImport(music.EmbeddedLyrics, null, token);
        if (document is null && stored is not null && parser.HasLyrics(stored.Document, token)) document = stored.Document;
        if (document is null && music.Id <= 0)
        {
            var cached = OneShotLyricsCache.Load(music.Path);
            externalRevision = cached?.Revision ?? 0;
            if (cached is not null && parser.HasLyrics(cached.Document, token)) document = cached.Document;
        }
        if (document is null && stored?.SourceKind != "User" && AppSettings.IsAutoLyricsEnabled)
        {
            document = await online.SearchAsync(music, false, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (document is not null)
            {
                if (stored is not null)
                {
                    bool saved = await database.Lyrics.SaveAsync(music.Id, document, stored.Revision, "Online", token: token).ConfigureAwait(false);
                    if (!saved) document = (await database.Lyrics.GetAsync(music.Id, token).ConfigureAwait(false)).Document;
                }
                else if (!OneShotLyricsCache.TrySave(music.Path, document, externalRevision))
                    document = OneShotLyricsCache.Load(music.Path)?.Document ?? document;
            }
        }
        token.ThrowIfCancellationRequested();
        return Project(document ?? LyricsDocument.Empty, music.Duration.TotalMilliseconds, token);
    }

    private async Task<LyricsDocument?> ReadLocalAsync(string musicPath, string order, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(musicPath)) return null;
        string? folder = Path.GetDirectoryName(musicPath);
        if (folder is null) return null;
        string stem = Path.GetFileNameWithoutExtension(musicPath);
        foreach (string ext in LyricsFilePolicy.Extensions(order))
        {
            token.ThrowIfCancellationRequested();
            string file = Path.ChangeExtension(musicPath, ext);
            if (!File.Exists(file)) continue;
            try
            {
                string content = await LyricsFilePolicy.ReadAsync(file, token).ConfigureAwait(false);
                var document = TryImport(content, null, token);
                if (document is null) continue;
                foreach (string name in LyricsFilePolicy.TranslationNames(stem, ext, order))
                {
                    string translationFile = Path.Combine(folder, name);
                    if (!File.Exists(translationFile)) continue;
                    try
                    {
                        string translation = await LyricsFilePolicy.ReadAsync(translationFile, token).ConfigureAwait(false);
                        string normalized = parser.NormalizeTranslation(translation, token);
                        if (string.IsNullOrWhiteSpace(normalized)) continue;
                        document = document with { TranslationLrc = normalized };
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                    { logger.LogWarning(ex, "Could not read translation: {Path}", translationFile); }
                }
                return document;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { logger.LogWarning(ex, "Could not read lyrics: {Path}", file); }
        }
        return null;
    }

    private LyricsDocument? TryImport(string original, string? translation, CancellationToken token)
    {
        try
        {
            var document = parser.Import(original, translation, token, AppData.SystemLanguage);
            return parser.HasLyrics(document, token) ? document : null;
        }
        catch (Exception ex) when (ex is FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            logger.LogWarning(ex, "Invalid lyrics input or embedded translation");
            var raw = new LyricsDocument(new(original, LyricsFormat.Unknown), null);
            return parser.HasLyrics(raw, token) ? parser.Import(original, token: token, extractEmbeddedTranslation: false) : null;
        }
    }

    private List<LyricLine> Project(LyricsDocument document, double duration, CancellationToken token)
    {
        var parsed = parser.Parse(document, duration, token);
        var result = new List<LyricLine>(parsed.Length);
        foreach (var source in parsed)
        {
            token.ThrowIfCancellationRequested();
            var line = new LyricLine { StartMs = source.StartMs, EndMs = source.EndMs, TransLateText = source.Translation };
            if (source.Words.Length > 0)
            {
                foreach (var word in source.Words)
                    line.Words.Add(new LyricWord { Word = word.Text, StartMs = word.StartMs,
                        DurationMs = Math.Max(0, word.EndMs - word.StartMs) });
            }
            else
            {
                var words = SplitEverything(source.Text);
                double span = Math.Max(0, source.EndMs - source.StartMs - 300);
                for (int i = 0; i < words.Count; i++)
                    line.Words.Add(new LyricWord { Word = words[i], StartMs = source.StartMs + span * i / words.Count, DurationMs = span / words.Count });
            }
            result.Add(line);
        }
        return result;
    }

    public static List<string> SplitEverything(string input)
    {
        if (string.IsNullOrEmpty(input)) return [];
        var result = new List<string>();
        var span = input.AsSpan();
        int i = 0;
        int end = span.Length;

        while (i < end)
        {
            char c = span[i];
            if (IsCjkLetter(c)) { result.Add(span.Slice(i++, 1).ToString()); continue; }
            if (char.IsLetterOrDigit(c))
            {
                int start = i;
                i++;
                while (i < end && char.IsLetterOrDigit(span[i]) && !IsCjkLetter(span[i])) i++;
                result.Add(span.Slice(start, i - start).ToString());
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                int start = i;
                i++;
                while (i < end && char.IsWhiteSpace(span[i])) i++;
                result.Add(span.Slice(start, i - start).ToString());
                continue;
            }
            result.Add(span.Slice(i++, 1).ToString());
        }
        return result;
    }

    private static bool IsCjkLetter(char c) =>
        (c >= '\u4E00' && c <= '\u9FFF') ||   // CJK Unified Ideographs
        (c >= '\u3040' && c <= '\u30FF') ||   // Hiragana + Katakana
        (c >= '\uAC00' && c <= '\uD7AF');     // Hangul Syllables

}
