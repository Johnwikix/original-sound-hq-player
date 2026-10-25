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

public interface ILlmTranslationService
{
    Task<string?> TranslateAsync(Music music, LyricsDocument document, CancellationToken token = default);
    Task<string?> TranslateManuallyAsync(Music music, LyricsDocument document, CancellationToken token = default)
        => TranslateAsync(music, document, token);
}

/// <summary>Source resolution and display projection. The caller owns cancellation and UI publication.</summary>
public sealed class LyricsRefreshService(MusicDatabaseService database, LyricsParser parser,
    LyricsOnlineSearch online, WebDavLibraryService webDav, ILlmTranslationService? llm,
    LyricsRomanizer romanizer, ILogger<LyricsRefreshService> logger)
{
    private const double LineEndOffsetMs = 300;

    // 保留回归工具和外部宿主的旧构造入口；没有翻译服务时仅执行原有歌词解析链。
    public LyricsRefreshService(MusicDatabaseService database, LyricsParser parser, LyricsOnlineSearch online,
        WebDavLibraryService webDav, ILogger<LyricsRefreshService> logger)
        : this(database, parser, online, webDav, null, new LyricsRomanizer(), logger) { }

    public async Task<List<LyricLine>> SetLyrics(Music music, CancellationToken token, Action<List<LyricLine>>? publishCached = null)
    {
        string order = AppSettings.LocalLyricsFormatOrder;
        bool databaseFirst = AppSettings.PreferDatabaseLyrics;
        LyricsSnapshot? stored = music.Id > 0 ? await database.Lyrics.GetAsync(music.Id, token).ConfigureAwait(false) : null;
        bool hasStoredLyrics = (databaseFirst || music.IsRemote) && stored is not null && parser.HasLyrics(stored.Document, token);
        LyricsDocument? document = null;
        long externalRevision = 0;
        bool userEdited = stored?.SourceKind == "User";
        LyricsCacheEntry? cached = null;
        if (databaseFirst)
        {
            if (hasStoredLyrics) document = stored!.Document;
            else if (music.Id <= 0)
            {
                // 一次性播放没有数据库行；已保存的歌词缓存承担相同的来源优先级。
                cached = OneShotLyricsCache.Load(music.Path);
                externalRevision = cached?.Revision ?? 0;
                userEdited = cached?.SourceKind == "User";
                if (cached is not null && parser.HasLyrics(cached.Document, token)) document = cached.Document;
            }
        }
        if (document is null)
        {
            if (!music.IsRemote) document = await ReadLocalAsync(music.Path, order, token).ConfigureAwait(false);
            else
            {
                if (hasStoredLyrics)
                    publishCached?.Invoke(Project(stored!.Document, music.Duration.TotalMilliseconds, token));
                document = await webDav.ReadLyricsDocumentAsync(music, order, token).ConfigureAwait(false);
            }
        }
        if (music.IsRemote && document is not null && stored is not null && stored.SourceKind != "User" &&
            document != stored.Document)
        {
            bool saved = await database.Lyrics.SaveAsync(music.Id, document, stored.Revision, "RemoteSidecar", music.Path, token).ConfigureAwait(false);
            stored = await database.Lyrics.GetAsync(music.Id, token).ConfigureAwait(false);
            if (!saved) document = stored.Document;
        }
        if (document is null && music.Id <= 0 && !string.IsNullOrWhiteSpace(music.EmbeddedLyrics))
            document = TryImport(music.EmbeddedLyrics, null, token);
        if (document is null && stored is not null)
        {
            if (!databaseFirst && !music.IsRemote) hasStoredLyrics = parser.HasLyrics(stored.Document, token);
            if (hasStoredLyrics) document = stored.Document;
        }
        if (document is null && music.Id <= 0 && !databaseFirst)
        {
            cached = OneShotLyricsCache.Load(music.Path);
            externalRevision = cached?.Revision ?? 0;
            userEdited = cached?.SourceKind == "User";
            if (cached is not null && parser.HasLyrics(cached.Document, token)) document = cached.Document;
        }
        if (document is null && !userEdited && AppSettings.IsAutoLyricsEnabled)
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

        if (llm is not null && document is not null && string.IsNullOrWhiteSpace(document.TranslationLrc) && parser.HasLyrics(document, token))
        {
            // 歌词原文先发布，网络请求在 ApplicationTasks 后台执行，不阻塞播放和首屏歌词。
            publishCached?.Invoke(Project(document, music.Duration.TotalMilliseconds, token));
            string? translation = await llm.TranslateAsync(music, document, token).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(translation))
            {
                var translated = document with { TranslationLrc = translation };
                bool accepted = true;
                if (music.Id > 0 && stored is not null)
                {
                    bool saved = await database.Lyrics.SaveAsync(music.Id, translated, stored.Revision,
                        "Llm", music.IsRemote ? music.Path : "", token).ConfigureAwait(false);
                    if (!saved)
                    {
                        var current = await database.Lyrics.GetAsync(music.Id, token).ConfigureAwait(false);
                        translated = current.Document;
                        accepted = false;
                    }
                }
                if (accepted && !music.IsRemote)
                {
                    try { await LyricsExporter.SaveTranslationFileAsync(music.Path, translation, token).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { logger.LogWarning(ex, "无法写入大模型翻译侧车文件: {Path}", music.Path); }
                }
                document = translated;
            }
        }
        string? generatedPronunciation = null;
        if (document is not null && string.IsNullOrWhiteSpace(document.PronunciationLrc)
            && AppSettings.IsLyricsPronunciationEnabled
            && parser.HasLyrics(document, token))
        {
            try
            {
                // The generated track exists only in this render snapshot. Never
                // put it into `document`, because that object is the persistence
                // boundary for the database, one-shot cache and sidecar exporter.
                generatedPronunciation = romanizer.GeneratePronunciationLrc(document,
                    music.Duration.TotalMilliseconds, parser, token);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "无法生成本地歌词发音: {Path}", music.Path);
            }
        }
        token.ThrowIfCancellationRequested();
        LyricsDocument displayDocument = document ?? LyricsDocument.Empty;
        if (!string.IsNullOrWhiteSpace(generatedPronunciation))
            displayDocument = displayDocument with { PronunciationLrc = generatedPronunciation };
        return Project(displayDocument, music.Duration.TotalMilliseconds, token);
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
                foreach (string name in LyricsFilePolicy.PronunciationNames(stem, ext, order))
                {
                    string pronunciationFile = Path.Combine(folder, name);
                    if (!File.Exists(pronunciationFile)) continue;
                    try
                    {
                        string pronunciation = await LyricsFilePolicy.ReadAsync(pronunciationFile, token).ConfigureAwait(false);
                        string normalized = parser.NormalizeTranslation(pronunciation, token);
                        if (string.IsNullOrWhiteSpace(normalized)) continue;
                        document = document with { PronunciationLrc = normalized };
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Xml.XmlException or OverflowException or System.Text.RegularExpressions.RegexMatchTimeoutException)
                    { logger.LogWarning(ex, "Could not read pronunciation: {Path}", pronunciationFile); }
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
        // 格式以内容为准，旧缓存可能记录过错误的格式。
        bool preserveExplicitTiming = parser.Detect(document.Original.Content) == LyricsFormat.Ttml;
        var parsed = parser.Parse(document, duration, token);
        // Explicit tags take precedence; untagged lines are routed by their script.
        // Han-only lyrics default to Mandarin because script alone cannot prove Yue.
        LyricsLanguage? documentLanguage = LyricsLanguagePolicy.Detect(document);
        bool allowPronunciation = documentLanguage is { } language
            && LyricsLanguagePolicy.IsEnabled(language);
        var result = new List<LyricLine>(parsed.Length);
        int nextDistinct = 0;
        for (int index = 0; index < parsed.Length; index++)
        {
            token.ThrowIfCancellationRequested();
            var source = parsed[index];
            bool allowLinePronunciation = allowPronunciation;
            if (documentLanguage is { } songLanguage
                && LyricsRomanizer.DetectLineLanguage(source.Text, songLanguage) is { } lineLanguage)
            {
                allowLinePronunciation = LyricsLanguagePolicy.IsEnabled(lineLanguage);
            }
            if (nextDistinct <= index)
            {
                nextDistinct = index + 1;
                while (nextDistinct < parsed.Length && parsed[nextDistinct].StartMs <= source.StartMs)
                    nextDistinct++;
            }
            // 旧格式的展示行尾一直由下一次入句决定；TTML 保留显式行尾及重叠。
            // 这些兼容处理只作用于发布快照，不改写解析缓存或持久化原文。
            double highlightEnd = nextDistinct < parsed.Length ? parsed[nextDistinct].StartMs
                : duration > 0 ? duration + 2000
                : preserveExplicitTiming ? source.EndMs : Math.Max(10500, source.StartMs + 2000);
            var line = new LyricLine
            {
                StartMs = source.StartMs,
                EndMs = preserveExplicitTiming ? source.EndMs : highlightEnd,
                HighlightEndMs = preserveExplicitTiming ? Math.Max(source.EndMs, highlightEnd) : highlightEnd,
                TransLateText = source.Translation,
                PronunciationText = allowLinePronunciation ? source.Pronunciation : string.Empty
            };
            if (source.Words.Length > 0)
            {
                foreach (var word in source.Words)
                {
                    double wordDuration = Math.Max(0, word.EndMs - word.StartMs);
                    if (preserveExplicitTiming)
                        line.Words.Add(new LyricWord { Word = word.Text, StartMs = word.StartMs, DurationMs = wordDuration });
                    else
                    {
                        // 旧分词边界也是字浮／重音效果的音节边界，不能只保持整行文本相同。
                        var words = SplitEverything(word.Text);
                        for (int i = 0; i < words.Count; i++)
                            line.Words.Add(new LyricWord { Word = words[i], StartMs = word.StartMs + wordDuration * i / words.Count,
                                DurationMs = wordDuration / words.Count });
                    }
                }
                if (!preserveExplicitTiming)
                    ApplyLegacyWordTiming(line);
            }
            else
            {
                var words = SplitEverything(source.Text);
                double span = Math.Max(0, line.EndMs - source.StartMs - LineEndOffsetMs);
                for (int i = 0; i < words.Count; i++)
                    line.Words.Add(new LyricWord { Word = words[i], StartMs = source.StartMs + span * i / words.Count, DurationMs = span / words.Count });
            }
            result.Add(line);
        }
        return result;
    }

    private static void ApplyLegacyWordTiming(LyricLine line)
    {
        if (line.Words.Count == 0) return;
        var lastWord = line.Words[^1];
        double originalSpan = lastWord.StartMs + lastWord.DurationMs - line.StartMs;
        if (originalSpan > 0)
        {
            // 与旧 FixEndMs 相同：压缩整行的偏移和时长，为末字动画回落留出 300ms。
            double scale = Math.Max(0, originalSpan - LineEndOffsetMs) / originalSpan;
            foreach (var word in line.Words)
            {
                word.StartMs = line.StartMs + (word.StartMs - line.StartMs) * scale;
                word.DurationMs *= scale;
            }
        }
        else
        {
            double perWord = Math.Max(0, line.EndMs - line.StartMs - LineEndOffsetMs) / line.Words.Count;
            for (int i = 0; i < line.Words.Count; i++)
            {
                line.Words[i].StartMs = line.StartMs + perWord * i;
                line.Words[i].DurationMs = perWord;
            }
        }
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
