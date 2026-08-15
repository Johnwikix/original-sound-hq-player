using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;

namespace WinUIMusicPlayer.Services
{
    public class LyricsRefreshService : IDisposable
    {
        // ──────────────────────────────────────────────────────────────
        //  静态 Regex：编译一次，全生命周期复用，避免每次解析重复构建
        // ──────────────────────────────────────────────────────────────

        // 格式探测：QRC 特征
        private static readonly Regex s_qrcDetect =
            new(@"<\d{2}:\d{2}\.\d{2,3}>", RegexOptions.Compiled);

        // 格式探测：KRC 特征
        private static readonly Regex s_krcDetect =
            new(@"^\[\d+,\d+\]", RegexOptions.Compiled | RegexOptions.Multiline);

        // 格式探测：逐字 LRC 特征（行首时间标签后紧接非空文本，再出现第二个时间标签）
        private static readonly Regex s_enhancedLrcDetect =
            new(@"^\[\d{1,2}:\d{2}\.\d{2,3}\][^\r\n\[]+\[\d{1,2}:\d{2}\.\d{2,3}\]",
                RegexOptions.Compiled | RegexOptions.Multiline);

        // 本地文件扩展名候选（静态，避免每次调用分配 array）
        private static readonly string[] s_lyricExtensions = [".krc", ".qrc", ".lrc"];

        // 行级时间偏移（ms）：每行动画在 EndMs 前提前结束，确保过渡平滑
        internal static double LineEndOffsetMs = 300;

        private static readonly ConcurrentBag<LyricLine> s_linePool = new();
        private static readonly ConcurrentBag<LyricWord> s_wordPool = new();
        private const int MaxPoolSize = 500;
        private List<LyricLine>? _previousLyrics;

        private static LyricLine RentLine()
        {
            if (s_linePool.TryTake(out var line))
            {
                line.Words.Clear();
                line.TransLateText = string.Empty;
                line.IsCurrent = false;
                line.StartMs = 0;
                line.EndMs = 0;
                return line;
            }
            return new LyricLine();
        }

        private static LyricWord RentWord()
        {
            if (s_wordPool.TryTake(out var word))
            {
                word.Word = string.Empty;
                word.StartMs = 0;
                word.DurationMs = 0;
                return word;
            }
            return new LyricWord();
        }

        public static void ReturnLyrics(List<LyricLine>? lyrics)
        {
            if (lyrics is null) return;
            foreach (var line in lyrics)
            {
                if (s_wordPool.Count < MaxPoolSize)
                {
                    foreach (var word in line.Words)
                        s_wordPool.Add(word);
                }
                line.Words.Clear();
                if (s_linePool.Count < MaxPoolSize)
                    s_linePool.Add(line);
            }
        }

        // ──────────────────────────────────────────────────────────────

        private CancellationTokenSource? _lyricsCancellationTokenSource;
        private MusicDatabaseService _musicDatabaseService { get; }
        private ILogger<LyricsRefreshService> _logger;

        public LyricsRefreshService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService, ILogger<LyricsRefreshService> logger)
        {
            _musicDatabaseService = musicDatabaseService;
            _logger = logger;
        }

        // ──────────────────────────────────────────────────────────────
        //  主入口
        // ──────────────────────────────────────────────────────────────

        public async Task<List<LyricLine>> SetLyrics(Music music)
        {
            CancelPreviousLyricsTask();
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var ct = _lyricsCancellationTokenSource.Token;

            ReturnLyrics(Interlocked.Exchange(ref _previousLyrics, null));

            try
            {
                await Task.Delay(500, ct);
                var (lyricsText, transLrc, krc, tKrc) = await _musicDatabaseService.GetLyricsAsync(music.Id);

                // 1. 本地文件（.krc / .qrc / .lrc）
                var localLyrics = TryParseLocalLyricsFile(music, ct);
                if (localLyrics is { Count: > 0 })
                {
                    music.PlayCount++;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    FixEndMs(localLyrics, music.Duration.TotalMilliseconds);
                    _previousLyrics = localLyrics;
                    return localLyrics;
                }

                // 2. music.Krc 缓存（KRC/QRC 在线）
                var (krcLyrics, krcOut, tKrcOut) = await TryParseKrcLyricsInternal(music, krc ?? "", tKrc ?? "", ct);
                if (krcLyrics.Count > 0)
                {
                    music.PlayCount++;
                    await _musicDatabaseService.SaveLyricsAsync(music.Id, lyricsText, transLrc, krcOut, tKrcOut);
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    FixEndMs(krcLyrics, music.Duration.TotalMilliseconds);
                    _previousLyrics = krcLyrics;
                    return krcLyrics;
                }

                // 3. LRC 缓存或在线搜索（本地文件已在步骤1处理）
                lyricsText ??= krcOut;
                var (lrcLyrics, lrcOut, transOut) = await ParseLrcLyricsInternal(music, lyricsText ?? "", transLrc ?? "", null, null, ct);

                ct.ThrowIfCancellationRequested();
                music.PlayCount++;
                await _musicDatabaseService.SaveLyricsAsync(music.Id, lrcOut, transOut, krcOut, tKrcOut);
                await _musicDatabaseService.UpdateMusicInfo(music);
                FixEndMs(lrcLyrics, music.Duration.TotalMilliseconds);
                _previousLyrics = lrcLyrics;
                return lrcLyrics;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }

        private static void FixEndMs(List<LyricLine> lyrics, double songDurationMs)
        {
            if (lyrics.Count == 0) return;
            double fallbackMs = songDurationMs > 0 ? songDurationMs + 2000 : 10500;
            for (int i = 0; i < lyrics.Count; i++)
            {
                var line = lyrics[i];
                line.EndMs = (i + 1 < lyrics.Count) ? lyrics[i + 1].StartMs : fallbackMs;

                int wc = line.Words.Count;
                if (wc > 0)
                {
                    var lastWord = line.Words[wc - 1];
                    double originalSpan = lastWord.StartMs + lastWord.DurationMs - line.StartMs;
                    if (originalSpan > 0)
                    {
                        double reducedMs = Math.Max(0, originalSpan - LineEndOffsetMs);
                        double scale = reducedMs / originalSpan;
                        for (int j = 0; j < wc; j++)
                        {
                            double offset = line.Words[j].StartMs - line.StartMs;
                            line.Words[j].StartMs = line.StartMs + offset * scale;
                            line.Words[j].DurationMs *= scale;
                        }
                    }
                    else
                    {
                        double reducedMs = Math.Max(0, line.EndMs - line.StartMs - LineEndOffsetMs);
                        double perMs = reducedMs / wc;
                        for (int j = 0; j < wc; j++)
                        {
                            line.Words[j].StartMs = line.StartMs + perMs * j;
                            line.Words[j].DurationMs = perMs;
                        }
                    }
                }
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  本地文件读取
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 按 .krc → .qrc → .lrc 顺序查找本地文件，自动识别格式并解析。
        /// </summary>
        private List<LyricLine>? TryParseLocalLyricsFile(Music music, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(music.Path)) return null;

            foreach (var ext in s_lyricExtensions)
            {
                try
                {
                    string filePath = Path.ChangeExtension(music.Path, ext);
                    if (!File.Exists(filePath)) continue;

                    string content = File.ReadAllText(filePath);
                    if (string.IsNullOrWhiteSpace(content)) continue;

                    string? transContent = TryReadTranslationFile(music.Path, ext);

                    ct.ThrowIfCancellationRequested();

                    var lyrics = ParseByFormat(content, transContent, ct);
                    if (lyrics is { Count: > 0 })
                        return lyrics;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger.LogWarning(ex, $"TryParseLocalLyricsFile 读取本地歌词文件失败，继续尝试下一个格式: {ex.Message}"); }
            }

            return null;
        }

        /// <summary>
        /// 读取翻译文件：原文件名_Translated{ext}，使用 string.Concat 避免插值分配
        /// </summary>
        private string? TryReadTranslationFile(string musicPath, string ext)
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(musicPath);
                string? dir = Path.GetDirectoryName(musicPath);
                if (string.IsNullOrEmpty(dir)) return null;

                string transPath = Path.Combine(dir, string.Concat(fileName, "_Translated", ext));
                return File.Exists(transPath) ? File.ReadAllText(transPath) : null;
            }
            catch (Exception ex) { _logger.LogWarning(ex, $"TryReadTranslationFile 读取翻译文件失败: {ex.Message}"); return null; }
        }

        // ──────────────────────────────────────────────────────────────
        //  格式判断与分发
        // ──────────────────────────────────────────────────────────────

        private List<LyricLine>? ParseByFormat(string content, string? transContent, CancellationToken ct)
        {
            List<LyricLine> lyrics;

            if (IsQrcFormat(content))
                lyrics = ParseQrcLyrics(content, ct);
            else if (IsKrcFormat(content))
                lyrics = ParseKrcLyrics(content, ct);
            else if (IsEnhancedLrcFormat(content))
                lyrics = ParseEnhancedLyrics(content, ct);
            else
                return SpliteContent(content, transContent, new List<LyricLine>());

            if (lyrics.Count > 0 && !string.IsNullOrWhiteSpace(transContent))
                MergeTranslation(lyrics, transContent);

            return lyrics;
        }

        private static bool IsQrcFormat(string content) =>
            !string.IsNullOrWhiteSpace(content) && s_qrcDetect.IsMatch(content);

        private static bool IsKrcFormat(string content) =>
            !string.IsNullOrWhiteSpace(content) && s_krcDetect.IsMatch(content);

        private static bool IsEnhancedLrcFormat(string content) =>
            !string.IsNullOrWhiteSpace(content) && s_enhancedLrcDetect.IsMatch(content);

        // ──────────────────────────────────────────────────────────────
        //  KRC 解析
        // ──────────────────────────────────────────────────────────────

        private async Task<(List<LyricLine> lyrics, string? krc, string? tKrc)> TryParseKrcLyricsInternal(
            Music music, string krc, string tKrc, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(krc) && AppSettings.IsAutoLyricsEnabled && !music.IsKrcSearched)
            {
                try
                {
                    var (newKrc, newTKrc) = await App.Services.GetRequiredService<LrcService>()
                        .GetKrcLyricsAsync(music, cancellationToken);
                    if (!string.IsNullOrEmpty(newKrc))
                    {
                        krc = newKrc;
                        tKrc = newTKrc ?? "";
                    }
                    music.IsKrcSearched = true;
                }
                catch (OperationCanceledException) { }
            }

            if (string.IsNullOrWhiteSpace(krc)) return ([], krc, tKrc);

            var lyrics = IsQrcFormat(krc)
                ? ParseQrcLyrics(krc, cancellationToken)
                : ParseKrcLyrics(krc, cancellationToken);

            if (lyrics.Count > 0 && !string.IsNullOrWhiteSpace(tKrc))
                MergeTranslation(lyrics, tKrc);

            return (lyrics, krc, tKrc);
        }

        /// <summary>
        /// 解析 KRC 格式：完全 Span 解析，零 string 分配
        /// </summary>
        private List<LyricLine> ParseKrcLyrics(string krc, CancellationToken cancellationToken = default)
        {
            var lyrics = new List<LyricLine>();
            cancellationToken.ThrowIfCancellationRequested();

            var span = krc.AsSpan();
            while (!span.IsEmpty)
            {
                int nl = span.IndexOfAny('\n', '\r');
                var lineSpan = nl >= 0 ? span[..nl] : span;
                span = nl >= 0 ? span[(nl + 1)..] : ReadOnlySpan<char>.Empty;

                var trimmed = lineSpan.Trim();
                if (trimmed.IsEmpty || trimmed[0] != '[') continue;

                if (trimmed.StartsWith("[ti:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[ar:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[al:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[by:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[offset:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[kana:", StringComparison.Ordinal))
                    continue;

                // 手动解析 [startMs,durationMs] 避免 regex + ToString()
                int bracketClose = trimmed.IndexOf(']');
                if (bracketClose < 1) continue;
                int comma = trimmed.Slice(1, bracketClose - 1).IndexOf(',');
                if (comma < 0) continue;
                comma++; // relative to trimmed

                if (!long.TryParse(trimmed.Slice(1, comma - 1), out long lineStartMs)) continue;

                var content = trimmed.Slice(bracketClose + 1);
                if (content.IsEmpty || content.IsWhiteSpace()) continue;

                var lyricLine = RentLine();
                lyricLine.StartMs = lineStartMs;
                ParseKrcWords(content, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            return lyrics;
        }

        /// <summary>
        /// KRC 字级解析：直接 Span 操作，字词直接加入为 LyricWord，免 SplitSpan 分配
        /// </summary>
        private static void ParseKrcWords(ReadOnlySpan<char> content, long lineStartMs, LyricLine lyricLine)
        {
            int i = 0;
            int end = content.Length;

            while (i < end)
            {
                int parenOpen = content.Slice(i).IndexOf('(');
                if (parenOpen == -1)
                {
                    var remaining = content.Slice(i);
                    if (!remaining.IsWhiteSpace())
                    {
                        foreach (var ch in SplitSpan(remaining))
                        {
                            var w = RentWord();
                            w.Word = ch;
                            w.StartMs = lineStartMs;
                            lyricLine.Words.Add(w);
                        }
                    }
                    break;
                }

                parenOpen += i;
                var wordSpan = content.Slice(i, parenOpen - i);

                int parenClose = content.Slice(parenOpen + 1).IndexOf(')');
                if (parenClose == -1) break;
                parenClose += parenOpen + 1;

                var timeSpan = content.Slice(parenOpen + 1, parenClose - parenOpen - 1);
                int commaIdx = timeSpan.IndexOf(',');
                if (commaIdx < 0) { i = parenClose + 1; continue; }

                if (!long.TryParse(timeSpan[..commaIdx], out long offsetMs) ||
                    !long.TryParse(timeSpan[(commaIdx + 1)..], out long durationMs))
                {
                    i = parenClose + 1;
                    continue;
                }

                if (!wordSpan.IsEmpty)
                {
                    if (wordSpan.IsWhiteSpace())
                    {
                        if (lyricLine.Words.Count > 0)
                        {
                            var last = lyricLine.Words[^1];
                            if (last.Word.Length > 0 && char.IsLetter(last.Word[^1]) && !IsCjkLetter(last.Word[^1]))
                                last.Word += " ";
                        }
                    }
                    else
                    {
                        var subWords = SplitSpan(wordSpan);
                        int wCount = subWords.Count;
                        double perMs = wCount > 0 ? (double)durationMs / wCount : durationMs;
                        for (int k = 0; k < wCount; k++)
                        {
                            var w = RentWord();
                            w.Word = subWords[k];
                            w.StartMs = offsetMs + perMs * k;
                            w.DurationMs = perMs;
                            lyricLine.Words.Add(w);
                        }
                    }
                }

                i = parenClose + 1;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  QRC 解析
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析 QRC 格式：完全 Span 解析，零 string 分配
        /// </summary>
        private List<LyricLine> ParseQrcLyrics(string qrc, CancellationToken cancellationToken = default)
        {
            var lyrics = new List<LyricLine>();
            cancellationToken.ThrowIfCancellationRequested();

            var span = qrc.AsSpan();
            while (!span.IsEmpty)
            {
                int nl = span.IndexOfAny('\n', '\r');
                var lineSpan = nl >= 0 ? span[..nl] : span;
                span = nl >= 0 ? span[(nl + 1)..] : ReadOnlySpan<char>.Empty;

                var trimmed = lineSpan.Trim();
                if (trimmed.IsEmpty || trimmed[0] != '[') continue;

                if (trimmed.StartsWith("[ti:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[ar:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[al:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[by:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[offset:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[kana:", StringComparison.Ordinal))
                    continue;

                // 手动解析 [mm:ss.xx] 避免 regex + ToString()
                int bracketClose = trimmed.IndexOf(']');
                if (bracketClose < 1) continue;
                var timePart = trimmed.Slice(1, bracketClose - 1);
                int colon = timePart.IndexOf(':');
                if (colon < 0) continue;
                int dotRel = timePart.Slice(colon + 1).IndexOf('.');
                if (dotRel < 0) continue;
                int dot = colon + 1 + dotRel;

                long lineStartMs = ParseQrcTimeToMs(
                    timePart.Slice(0, colon),
                    timePart.Slice(colon + 1, dot - colon - 1),
                    timePart.Slice(dot + 1));

                var content = trimmed.Slice(bracketClose + 1);
                if (content.IsEmpty || content.IsWhiteSpace()) continue;

                var lyricLine = RentLine();
                lyricLine.StartMs = lineStartMs;
                ParseQrcWords(content, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            return lyrics;
        }

        /// <summary>
        /// QRC 字级解析：手动 Span 扫描 &lt;mm:ss.xx&gt; 标签，免 string + regex 分配
        /// </summary>
        private static void ParseQrcWords(ReadOnlySpan<char> content, long lineStartMs, LyricLine lyricLine)
        {
            int i = 0;
            while (i < content.Length)
            {
                int tagStart = content.Slice(i).IndexOf('<');
                if (tagStart < 0)
                {
                    var remaining = content.Slice(i);
                    if (!remaining.IsEmpty && !remaining.IsWhiteSpace())
                    {
                        foreach (var ch in SplitSpan(remaining))
                        {
                            var w = RentWord();
                            w.Word = ch;
                            w.StartMs = lineStartMs;
                            lyricLine.Words.Add(w);
                        }
                    }
                    break;
                }

                tagStart += i;
                int tagEnd = content.Slice(tagStart).IndexOf('>');
                if (tagEnd < 0) break;
                tagEnd += tagStart;

                if (!TryParseQrcTagTime(content.Slice(tagStart + 1, tagEnd - tagStart - 1), out long segStartMs))
                {
                    i = tagEnd + 1;
                    continue;
                }

                // 文字在 > 之后到下一个 < 或结尾
                int textStart = tagEnd + 1;
                int nextTag = content.Slice(textStart).IndexOf('<');
                int textEnd = nextTag >= 0 ? textStart + nextTag : content.Length;

                long segEndMs = segStartMs;
                if (nextTag >= 0)
                {
                    int nextTagStart = textEnd;
                    int nextTagEnd = content.Slice(nextTagStart).IndexOf('>');
                    if (nextTagEnd >= 0)
                    {
                        _ = TryParseQrcTagTime(
                            content.Slice(nextTagStart + 1, nextTagEnd - 1),
                            out segEndMs);
                    }
                }

                var textSpan = content.Slice(textStart, textEnd - textStart);
                if (!textSpan.IsEmpty)
                {
                    long segDurationMs = Math.Max(0, segEndMs - segStartMs);
                    var subWords = SplitSpan(textSpan);
                    int wordCount = subWords.Count;
                    if (wordCount > 0)
                    {
                        double perMs = segDurationMs > 0 ? (double)segDurationMs / wordCount : 0;
                        for (int k = 0; k < wordCount; k++)
                        {
                            var w = RentWord();
                            w.Word = subWords[k];
                            w.StartMs = segStartMs + perMs * k;
                            w.DurationMs = perMs;
                            lyricLine.Words.Add(w);
                        }
                    }
                }

                i = textEnd;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  Enhanced LRC 解析
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析逐字 LRC 格式（一行多个 [mm:ss.xx] 标签，标签后文本在该时刻开始，
        /// 行尾标签同时代表上一字符结束与下一行字符开始）：完全 Span 解析，零 string 分配
        /// </summary>
        private List<LyricLine> ParseEnhancedLyrics(string content, CancellationToken cancellationToken = default)
        {
            var lyrics = new List<LyricLine>();
            cancellationToken.ThrowIfCancellationRequested();

            var span = content.AsSpan();
            while (!span.IsEmpty)
            {
                int nl = span.IndexOfAny('\n', '\r');
                var lineSpan = nl >= 0 ? span[..nl] : span;
                span = nl >= 0 ? span[(nl + 1)..] : ReadOnlySpan<char>.Empty;

                var trimmed = lineSpan.Trim();
                if (trimmed.IsEmpty || trimmed[0] != '[') continue;

                if (trimmed.StartsWith("[ti:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[ar:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[al:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[by:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[offset:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[kana:", StringComparison.Ordinal))
                    continue;

                LyricLine? lyricLine = null;
                long lineStartMs = 0;
                long lineEndMs = 0;
                bool hasLineStart = false;

                int pos = 0;
                int end = trimmed.Length;
                while (pos < end)
                {
                    int bracketClose = trimmed.Slice(pos).IndexOf(']');
                    if (bracketClose < 1) break;
                    bracketClose += pos;

                    if (!TryParseQrcTagTime(trimmed.Slice(pos + 1, bracketClose - pos - 1), out long tagMs))
                        break;

                    if (!hasLineStart)
                    {
                        lineStartMs = tagMs;
                        hasLineStart = true;
                    }
                    lineEndMs = tagMs;

                    // 标签后文本扫描到下一个 [ 或行尾
                    int textStart = bracketClose + 1;
                    int nextTag = trimmed.Slice(textStart).IndexOf('[');
                    int textEnd = nextTag >= 0 ? textStart + nextTag : end;

                    long segEndMs = tagMs;
                    if (nextTag >= 0)
                    {
                        int nextBracketClose = trimmed.Slice(textEnd).IndexOf(']');
                        if (nextBracketClose >= 1 &&
                            TryParseQrcTagTime(trimmed.Slice(textEnd + 1, nextBracketClose - 1), out long nextMs))
                            segEndMs = nextMs;
                    }

                    var textSpan = trimmed.Slice(textStart, textEnd - textStart);
                    if (!textSpan.IsEmpty)
                    {
                        lyricLine ??= RentLine();
                        lyricLine.StartMs = lineStartMs;

                        long segDurationMs = Math.Max(0, segEndMs - tagMs);
                        var subWords = SplitSpan(textSpan);
                        int wordCount = subWords.Count;
                        if (wordCount > 0)
                        {
                            double perMs = segDurationMs > 0 ? (double)segDurationMs / wordCount : 0;
                            for (int k = 0; k < wordCount; k++)
                            {
                                var w = RentWord();
                                w.Word = subWords[k];
                                w.StartMs = tagMs + perMs * k;
                                w.DurationMs = perMs;
                                lyricLine.Words.Add(w);
                            }
                        }
                    }

                    if (nextTag < 0) break;
                    pos = textEnd;
                }

                if (lyricLine is { Words.Count: > 0 })
                {
                    lyricLine.EndMs = lineEndMs;
                    lyrics.Add(lyricLine);
                }
            }

            return lyrics;
        }

        private static bool TryParseQrcTagTime(ReadOnlySpan<char> tagSpan, out long ms)
        {
            ms = 0;
            int colon = tagSpan.IndexOf(':');
            if (colon < 0) return false;
            int dotRel = tagSpan.Slice(colon + 1).IndexOf('.');
            if (dotRel < 0) return false;
            int dot = colon + 1 + dotRel;
            ms = ParseQrcTimeToMs(
                tagSpan.Slice(0, colon),
                tagSpan.Slice(colon + 1, dot - colon - 1),
                tagSpan.Slice(dot + 1));
            return true;
        }

        /// <summary>
        /// QRC 时间转毫秒：接收 ReadOnlySpan&lt;char&gt;，全程无 string 分配
        /// </summary>
        private static long ParseQrcTimeToMs(ReadOnlySpan<char> mm, ReadOnlySpan<char> ss, ReadOnlySpan<char> msSpan)
        {
            int minutes = int.Parse(mm);
            int seconds = int.Parse(ss);
            int milliseconds = msSpan.Length == 2 ? int.Parse(msSpan) * 10 : int.Parse(msSpan);
            return minutes * 60000L + seconds * 1000L + milliseconds;
        }

        // ──────────────────────────────────────────────────────────────
        //  翻译合并（KRC/QRC 共用）
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 手动 for loop 替代 LINQ + 匿名型别，零额外分配
        /// </summary>
        private void MergeTranslation(List<LyricLine> lyrics, string transContent)
        {
            ParseLrcToLines(transContent, (timeMs, transText) =>
            {
                double bestDiff = 101.0;
                LyricLine? bestLine = null;
                for (int i = 0; i < lyrics.Count; i++)
                {
                    double diff = Math.Abs(lyrics[i].StartMs - timeMs);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestLine = lyrics[i];
                    }
                }
                if (bestLine != null)
                    bestLine.TransLateText = transText;
            });
        }

        // ──────────────────────────────────────────────────────────────
        //  LRC 解析
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 从 music.Lyrics 缓存或在线搜索获取内容，并自动判断格式（LRC/KRC/QRC）解析。
        /// 本地文件已由 TryParseLocalLyricsFile 处理，此处不再读取本地文件。
        /// </summary>
        private async Task<(List<LyricLine> lyrics, string? lrc, string? trans)> ParseLrcLyricsInternal(
            Music music, string lrcContent, string transLrcStr, string? providedLrc, string? providedTrans, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                lrcContent = providedLrc;
                transLrcStr = string.IsNullOrWhiteSpace(transLrcStr) ? providedTrans : transLrcStr;

                if (string.IsNullOrWhiteSpace(lrcContent) && AppSettings.IsAutoLyricsEnabled && !music.IsLrcSearched)
                {
                    try
                    {
                        var (lyric, trans) = await App.Services.GetRequiredService<LrcService>()
                            .GetMixedLyricsAsync(music, cancellationToken);
                        if (!string.IsNullOrEmpty(lyric))
                        {
                            lrcContent = lyric;
                            transLrcStr = trans;
                        }
                        music.IsLrcSearched = true;
                    }
                    catch (OperationCanceledException) { }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                var parsed = ParseByFormat(lrcContent, transLrcStr, cancellationToken);
                if (parsed is { Count: > 0 })
                    return (parsed, lrcContent, transLrcStr);
            }

            var emptyLine = RentLine();
            emptyLine.IsCurrent = true;
            return ([emptyLine], lrcContent, transLrcStr);
        }

        // ──────────────────────────────────────────────────────────────
        //  通用工具方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 零分配分词：手动 Span 扫描，替换 Regex
        /// </summary>
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

        private static List<string> SplitSpan(ReadOnlySpan<char> input)
        {
            if (input.IsEmpty) return [];
            var result = new List<string>();
            int i = 0;
            int end = input.Length;

            while (i < end)
            {
                char c = input[i];
                if (IsCjkLetter(c)) { result.Add(input.Slice(i++, 1).ToString()); continue; }
                if (char.IsLetterOrDigit(c))
                {
                    int start = i;
                    i++;
                    while (i < end && char.IsLetterOrDigit(input[i]) && !IsCjkLetter(input[i])) i++;
                    result.Add(input.Slice(start, i - start).ToString());
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    int start = i;
                    i++;
                    while (i < end && char.IsWhiteSpace(input[i])) i++;
                    result.Add(input.Slice(start, i - start).ToString());
                    continue;
                }
                result.Add(input.Slice(i++, 1).ToString());
            }
            return result;
        }

        private static bool IsCjkLetter(char c) =>
            (c >= '\u4E00' && c <= '\u9FFF') ||   // CJK Unified Ideographs
            (c >= '\u3040' && c <= '\u30FF') ||   // Hiragana + Katakana
            (c >= '\uAC00' && c <= '\uD7AF');     // Hangul Syllables

        private List<LyricLine> SpliteContent(string lrcContent, string? transLrc, List<LyricLine> lyrics)
        {
            lyrics.Clear();

            // 1. 解析原文
            ParseLrcToLines(lrcContent, (timeMs, text) =>
            {
                var line = RentLine();
                line.StartMs = timeMs;
                foreach (var w in SplitEverything(text))
                {
                    var word = RentWord();
                    word.Word = w;
                    line.Words.Add(word);
                }
                lyrics.Add(line);
            });

            if (!string.IsNullOrEmpty(transLrc))
            {
                ParseLrcToLines(transLrc, (timeMs, transText) =>
                {
                    for (int i = 0; i < lyrics.Count; i++)
                    {
                        if (Math.Abs(lyrics[i].StartMs - timeMs) <= 50)
                        {
                            lyrics[i].TransLateText = transText;
                            break;
                        }
                    }
                });
            }

            lyrics.Sort(static (a, b) => a.StartMs.CompareTo(b.StartMs));

            return lyrics;
        }

        /// <summary>
        /// LRC 时间行解析：手动 Span 扫描 [mm:ss.xx]，零 Regex 分配。
        /// 纠错规则：
        /// 1. 行首连续多个时间标签视为重复出现行，每个标签各生成一条歌词；
        /// 2. 只有时间标签、无任何文本的行视为垃圾行，直接丢弃；
        /// 3. 单标签行行为与旧版完全一致。
        /// </summary>
        private void ParseLrcToLines(string content, Action<double, string> onLineParsed)
        {
            if (string.IsNullOrEmpty(content)) return;

            var span = content.AsSpan();
            while (!span.IsEmpty)
            {
                int nl = span.IndexOfAny('\n', '\r');
                var lineSpan = nl >= 0 ? span[..nl] : span;
                span = nl >= 0 ? span[(nl + 1)..] : ReadOnlySpan<char>.Empty;

                var trimmed = lineSpan.Trim();
                if (trimmed.IsEmpty || trimmed[0] != '[') continue;

                int tagCount = CountLeadingLrcTags(trimmed, out int textStart);
                if (tagCount == 0) continue;

                var textSpan = trimmed.Slice(textStart).Trim();
                if (textSpan.IsEmpty) continue;
                if (textSpan.Length == 2 && textSpan[0] == '/' && textSpan[1] == '/') continue;

                if (tagCount == 1)
                {
                    if (TryParseLrcTime(trimmed.Slice(1, textStart - 2), out double timeMs))
                        onLineParsed(timeMs, textSpan.ToString());
                    continue;
                }

                // 多时间标签：每个标签处各生成一条歌词，同一行内相同时间不重复
                int pos = 0;
                long lastTagMs = -1;
                while (pos < trimmed.Length && trimmed[pos] == '[')
                {
                    int bracketClose = trimmed.Slice(pos).IndexOf(']');
                    if (bracketClose < 1) break;
                    bracketClose += pos;
                    if (!TryParseLrcTime(trimmed.Slice(pos + 1, bracketClose - pos - 1), out double tagMs)) break;
                    if (tagMs != lastTagMs)
                    {
                        onLineParsed(tagMs, textSpan.ToString());
                        lastTagMs = (long)tagMs;
                    }
                    pos = bracketClose + 1;
                }
            }
        }

        /// <summary>
        /// 统计行首连续有效时间标签的数量，textStart 指向最后一个标签之后的位置
        /// </summary>
        private static int CountLeadingLrcTags(ReadOnlySpan<char> line, out int textStart)
        {
            int pos = 0;
            int count = 0;
            while (pos < line.Length && line[pos] == '[')
            {
                int bracketClose = line.Slice(pos).IndexOf(']');
                if (bracketClose < 1) break;
                bracketClose += pos;
                if (!TryParseLrcTime(line.Slice(pos + 1, bracketClose - pos - 1), out _)) break;
                count++;
                pos = bracketClose + 1;
            }
            textStart = pos;
            return count;
        }

        /// <summary>
        /// 解析 [mm:ss.xx] / [mm:ss:xx] 时间标签为毫秒
        /// </summary>
        private static bool TryParseLrcTime(ReadOnlySpan<char> timePart, out double timeMs)
        {
            timeMs = 0;
            int colon = timePart.IndexOf(':');
            if (colon < 0) return false;
            if (!int.TryParse(timePart.Slice(0, colon), out int minutes)) return false;

            var afterMin = timePart.Slice(colon + 1);
            int sep = afterMin.IndexOfAny('.', ':');
            if (sep < 0) return false;
            if (!int.TryParse(afterMin.Slice(0, sep), out int seconds)) return false;

            var msSpan = afterMin.Slice(sep + 1);
            if (!int.TryParse(msSpan, out int msRaw)) return false;
            int milliseconds = msSpan.Length == 2 ? msRaw * 10 : msRaw;

            timeMs = (minutes * 60 + seconds) * 1000.0 + milliseconds;
            return true;
        }

        // ──────────────────────────────────────────────────────────────
        //  取消与释放
        // ──────────────────────────────────────────────────────────────

        private void CancelPreviousLyricsTask()
        {
            if (_lyricsCancellationTokenSource is not null)
            {
                try
                {
                    if (!_lyricsCancellationTokenSource.IsCancellationRequested)
                        _lyricsCancellationTokenSource.Cancel();
                }
                catch (ObjectDisposedException) { }
                finally
                {
                    _lyricsCancellationTokenSource.Dispose();
                    _lyricsCancellationTokenSource = null;
                }
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
                CancelPreviousLyricsTask();
        }
    }
}