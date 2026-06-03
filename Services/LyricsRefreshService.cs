using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
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
        //  靜態 Regex：編譯一次，全生命週期復用，避免每次解析重複構建
        // ──────────────────────────────────────────────────────────────

        // 格式探測：QRC 特徵
        private static readonly Regex s_qrcDetect =
            new(@"<\d{2}:\d{2}\.\d{2,3}>", RegexOptions.Compiled);

        // 格式探測：KRC 特徵
        private static readonly Regex s_krcDetect =
            new(@"^\[\d+,\d+\]", RegexOptions.Compiled | RegexOptions.Multiline);

        // 本地文件擴展名候選（靜態，避免每次調用分配 array）
        private static readonly string[] s_lyricExtensions = [".krc", ".qrc", ".lrc"];

        // 行级时间偏移（ms）：每行动画在 EndMs 前提前结束，确保过渡平滑
        internal static double LineEndOffsetMs = 400;

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
                    return localLyrics;
                }

                // 2. music.Krc 緩存（KRC/QRC 在線）
                var (krcLyrics, krcOut, tKrcOut) = await TryParseKrcLyricsInternal(music, krc ?? "", tKrc ?? "", ct);
                if (krcLyrics.Count > 0)
                {
                    music.PlayCount++;
                    await _musicDatabaseService.SaveLyricsAsync(music.Id, lyricsText, transLrc, krcOut, tKrcOut);
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    FixEndMs(krcLyrics, music.Duration.TotalMilliseconds);
                    return krcLyrics;
                }

                // 3. LRC 緩存或在線搜索（本地文件已在步驟1處理）
                lyricsText ??= krcOut;
                var (lrcLyrics, lrcOut, transOut) = await ParseLrcLyricsInternal(music, lyricsText ?? "", transLrc ?? "", null, null, ct);

                ct.ThrowIfCancellationRequested();
                music.PlayCount++;
                await _musicDatabaseService.SaveLyricsAsync(music.Id, lrcOut, transOut, krcOut, tKrcOut);
                await _musicDatabaseService.UpdateMusicInfo(music);
                FixEndMs(lrcLyrics, music.Duration.TotalMilliseconds);
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
        //  本地文件讀取
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 按 .krc → .qrc → .lrc 順序查找本地文件，自動識別格式並解析。
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
        /// 讀取翻譯文件：原文件名_Translated{ext}，使用 string.Concat 避免插值分配
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
        //  格式判斷與分發
        // ──────────────────────────────────────────────────────────────

        private List<LyricLine>? ParseByFormat(string content, string? transContent, CancellationToken ct)
        {
            List<LyricLine> lyrics;

            if (IsQrcFormat(content))
                lyrics = ParseQrcLyrics(content, ct);
            else if (IsKrcFormat(content))
                lyrics = ParseKrcLyrics(content, ct);
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

                // 手動解析 [startMs,durationMs] 避免 regex + ToString()
                int bracketClose = trimmed.IndexOf(']');
                if (bracketClose < 1) continue;
                int comma = trimmed.Slice(1, bracketClose - 1).IndexOf(',');
                if (comma < 0) continue;
                comma++; // relative to trimmed

                if (!long.TryParse(trimmed.Slice(1, comma - 1), out long lineStartMs)) continue;

                var content = trimmed.Slice(bracketClose + 1);
                if (content.IsEmpty || content.IsWhiteSpace()) continue;

                var lyricLine = new LyricLine { StartMs = lineStartMs, IsCurrent = false };
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
                            lyricLine.Words.Add(new LyricWord
                            {
                                Word = ch,
                                StartMs = lineStartMs,
                                DurationMs = 0
                            });
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
                            lyricLine.Words.Add(new LyricWord
                            {
                                Word = subWords[k],
                                StartMs = offsetMs + perMs * k,
                                DurationMs = perMs
                            });
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

                // 手動解析 [mm:ss.xx] 避免 regex + ToString()
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

                var lyricLine = new LyricLine { StartMs = lineStartMs, IsCurrent = false };
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
                            lyricLine.Words.Add(new LyricWord
                            {
                                Word = ch,
                                StartMs = lineStartMs,
                                DurationMs = 0
                            });
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

                // 文字在 > 之後到下一個 < 或結尾
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
                            lyricLine.Words.Add(new LyricWord
                            {
                                Word = subWords[k],
                                StartMs = segStartMs + perMs * k,
                                DurationMs = perMs
                            });
                        }
                    }
                }

                i = textEnd;
            }
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
        /// QRC 時間轉毫秒：接收 ReadOnlySpan&lt;char&gt;，全程無 string 分配
        /// </summary>
        private static long ParseQrcTimeToMs(ReadOnlySpan<char> mm, ReadOnlySpan<char> ss, ReadOnlySpan<char> msSpan)
        {
            int minutes = int.Parse(mm);
            int seconds = int.Parse(ss);
            int milliseconds = msSpan.Length == 2 ? int.Parse(msSpan) * 10 : int.Parse(msSpan);
            return minutes * 60000L + seconds * 1000L + milliseconds;
        }

        // ──────────────────────────────────────────────────────────────
        //  翻譯合併（KRC/QRC 共用）
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 手動 for loop 替代 LINQ + 匿名型別，零額外分配
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
        /// 從 music.Lyrics 緩存或在線搜索獲取內容，並自動判斷格式（LRC/KRC/QRC）解析。
        /// 本地文件已由 TryParseLocalLyricsFile 處理，此處不再讀取本地文件。
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

            return ([new LyricLine { StartMs = 0, IsCurrent = true }], lrcContent, transLrcStr);
        }

        // ──────────────────────────────────────────────────────────────
        //  通用工具方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 零分配分詞：手動 Span 掃描，替換 Regex
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
                var line = new LyricLine { StartMs = timeMs, IsCurrent = false };
                foreach (var w in SplitEverything(text))
                    line.Words.Add(new LyricWord { Word = w });
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
        /// LRC 時間行解析：手動 Span 掃描 [mm:ss.xx]，零 Regex 分配
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

                int bracketClose = trimmed.IndexOf(']');
                if (bracketClose < 2) continue;
                var timePart = trimmed.Slice(1, bracketClose - 1);

                int colon = timePart.IndexOf(':');
                if (colon < 0) continue;
                if (!int.TryParse(timePart.Slice(0, colon), out int minutes)) continue;

                var afterMin = timePart.Slice(colon + 1);
                int sep = afterMin.IndexOfAny('.', ':');
                if (sep < 0) continue;
                if (!int.TryParse(afterMin.Slice(0, sep), out int seconds)) continue;

                var msSpan = afterMin.Slice(sep + 1);
                if (!int.TryParse(msSpan, out int msRaw)) continue;
                int milliseconds = msSpan.Length == 2 ? msRaw * 10 : msRaw;

                var textSpan = trimmed.Slice(bracketClose + 1).Trim();
                if (textSpan.IsEmpty) continue;
                if (textSpan.Length == 2 && textSpan[0] == '/' && textSpan[1] == '/') continue;

                onLineParsed((minutes * 60 + seconds) * 1000.0 + milliseconds, textSpan.ToString());
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  取消與釋放
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