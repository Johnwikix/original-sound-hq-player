using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Controls.Lyrics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.WebService;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class LyricsRefreshService : IDisposable
    {
        // ──────────────────────────────────────────────────────────────
        //  靜態 Regex：編譯一次，全生命週期復用，避免每次解析重複構建
        // ──────────────────────────────────────────────────────────────

        // KRC：行 [startMs,durationMs]內容
        private static readonly Regex s_krcLinePattern =
            new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);

        // QRC：行 [mm:ss.xx]內容
        private static readonly Regex s_qrcLinePattern =
            new(@"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)$", RegexOptions.Compiled);

        // QRC：字級標籤 <mm:ss.xx>
        private static readonly Regex s_qrcWordPattern =
            new(@"<(\d{2}):(\d{2})\.(\d{2,3})>", RegexOptions.Compiled);

        // LRC：時間標籤 [mm:ss.xx]
        private static readonly Regex s_lrcTimePattern =
            new(@"\[(\d{2}):(\d{2})([.:])(\d{2,3})\]", RegexOptions.Compiled);

        // 格式探測：QRC 特徵
        private static readonly Regex s_qrcDetect =
            new(@"<\d{2}:\d{2}\.\d{2,3}>", RegexOptions.Compiled);

        // 格式探測：KRC 特徵
        private static readonly Regex s_krcDetect =
            new(@"^\[\d+,\d+\]", RegexOptions.Compiled | RegexOptions.Multiline);

        // SplitEverything 分詞
        private static readonly Regex s_splitPattern =
            new(@"[\u4e00-\u9fa5]|[\u3040-\u30ff]|[\p{L}\p{N}]+|\s+|.",
                RegexOptions.Compiled);

        // 本地文件擴展名候選（靜態，避免每次調用分配 array）
        private static readonly string[] s_lyricExtensions = [".krc", ".qrc", ".lrc"];

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
                // 防抖
                await Task.Delay(500, ct);

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
                var krcLyrics = await TryParseKrcLyrics(music, ct);
                if (krcLyrics.Count > 0)
                {
                    music.PlayCount++;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    FixEndMs(krcLyrics, music.Duration.TotalMilliseconds);
                    return krcLyrics;
                }

                // 3. LRC 緩存或在線搜索（本地文件已在步驟1處理）
                var lyrics = await ParseLrcLyrics(music, null, null, ct);

                ct.ThrowIfCancellationRequested();
                music.PlayCount++;
                await _musicDatabaseService.UpdateMusicInfo(music);
                FixEndMs(lyrics, music.Duration.TotalMilliseconds);
                return lyrics;
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
                lyrics[i].EndMs = (i + 1 < lyrics.Count) ? lyrics[i + 1].StartMs : fallbackMs;
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

        private async Task<List<LyricLine>> TryParseKrcLyrics(Music music, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(music.Krc) && AppSettings.IsAutoLyricsEnabled && !music.IsKrcSearched)
            {
                try
                {
                    var (krc, tkrc) = await App.Services.GetRequiredService<LrcService>()
                        .GetKrcLyricsAsync(music, cancellationToken);
                    if (!string.IsNullOrEmpty(krc))
                    {
                        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                        {
                            music.Krc = krc;
                            music.TKrc = tkrc;
                        });
                    }
                    music.IsKrcSearched = true;
                }
                catch (OperationCanceledException) { }
            }

            if (string.IsNullOrWhiteSpace(music.Krc)) return [];

            var lyrics = IsQrcFormat(music.Krc)
                ? ParseQrcLyrics(music.Krc, cancellationToken)
                : ParseKrcLyrics(music.Krc, cancellationToken);

            if (lyrics.Count > 0 && !string.IsNullOrWhiteSpace(music.TKrc))
                MergeTranslation(lyrics, music.TKrc);

            return lyrics;
        }

        /// <summary>
        /// 解析 KRC 格式：Span 行迭代避免 Split string[]
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

                // Span.StartsWith 無分配
                if (trimmed.StartsWith("[ti:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[ar:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[al:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[by:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[offset:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("[kana:", StringComparison.Ordinal))
                    continue;

                string lineStr = trimmed.ToString();
                var lineMatch = s_krcLinePattern.Match(lineStr);
                if (!lineMatch.Success) continue;

                if (!long.TryParse(lineMatch.Groups[1].ValueSpan, out long lineStartMs)) continue;

                var contentGroup = lineMatch.Groups[3];
                if (contentGroup.Length == 0) continue;
                if (lineStr.AsSpan(contentGroup.Index, contentGroup.Length).IsWhiteSpace()) continue;

                var lyricLine = new LyricLine
                {
                    StartMs = lineStartMs,
                    IsCurrent = false
                };

                ParseKrcWords(lineStr, contentGroup.Index, contentGroup.Length, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            for (int i = 0; i < lyrics.Count; i++)
                lyrics[i].EndMs = (i + 1 < lyrics.Count) ? lyrics[i + 1].StartMs : lyrics[i].StartMs + 10000;

            return lyrics;
        }

        /// <summary>
        /// KRC 字級解析：Span 手動找逗號替代 Split(',')，零中間字符串分配
        /// </summary>
        private void ParseKrcWords(string line, int contentStart, int contentLength, long lineStartMs, LyricLine lyricLine)
        {
            int end = contentStart + contentLength;
            int i = contentStart;

            while (i < end)
            {
                int parenOpen = line.IndexOf('(', i, end - i);
                if (parenOpen == -1)
                {
                    var remaining = line.AsSpan(i, end - i);
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

                var wordSpan = line.AsSpan(i, parenOpen - i);

                int parenClose = line.IndexOf(')', parenOpen + 1, end - parenOpen - 1);
                if (parenClose == -1) break;

                // 手動 Span 找逗號，替代 timeStr.Split(',')
                var timeSpan = line.AsSpan(parenOpen + 1, parenClose - parenOpen - 1);
                int commaIdx = timeSpan.IndexOf(',');
                if (commaIdx < 0) { i = parenClose + 1; continue; }

                if (!long.TryParse(timeSpan[..commaIdx], out long offsetMs) ||
                    !long.TryParse(timeSpan[(commaIdx + 1)..], out long durationMs))
                {
                    i = parenClose + 1;
                    continue;
                }

                if (!wordSpan.IsEmpty && !wordSpan.IsWhiteSpace())
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

                i = parenClose + 1;
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  QRC 解析
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 解析 QRC 格式：Span 行迭代 + ParseQrcTimeToMs 接收 Span，全程無中間字符串分配
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

                string lineStr = trimmed.ToString();
                var lineMatch = s_qrcLinePattern.Match(lineStr);
                if (!lineMatch.Success) continue;

                long lineStartMs = ParseQrcTimeToMs(
                    lineStr.AsSpan(lineMatch.Groups[1].Index, lineMatch.Groups[1].Length),
                    lineStr.AsSpan(lineMatch.Groups[2].Index, lineMatch.Groups[2].Length),
                    lineStr.AsSpan(lineMatch.Groups[3].Index, lineMatch.Groups[3].Length));

                var contentGroup = lineMatch.Groups[4];
                if (contentGroup.Length == 0) continue;
                if (lineStr.AsSpan(contentGroup.Index, contentGroup.Length).IsWhiteSpace()) continue;

                var lyricLine = new LyricLine
                {
                    StartMs = lineStartMs,
                    IsCurrent = false
                };

                ParseQrcWords(lineStr, contentGroup.Index, contentGroup.Length, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            for (int i = 0; i < lyrics.Count; i++)
                lyrics[i].EndMs = (i + 1 < lyrics.Count) ? lyrics[i + 1].StartMs : lyrics[i].StartMs + 10000;

            return lyrics;
        }

        /// <summary>
        /// QRC 字級解析：雙指針直接掃描，省去中間 List&lt;(long,string)&gt; 分配；
        /// 下一個標籤時間在同一趟循環中讀取，無重複工作
        /// </summary>
        private void ParseQrcWords(string line, int contentStart, int contentLength, long lineStartMs, LyricLine lyricLine)
        {
            string contentStr = line.AsSpan(contentStart, contentLength).ToString();
            var tagMatches = s_qrcWordPattern.Matches(contentStr);

            if (tagMatches.Count == 0)
            {
                var stripped = s_qrcWordPattern.Replace(contentStr, "").AsSpan().Trim();
                if (!stripped.IsEmpty)
                {
                    foreach (var ch in SplitSpan(stripped))
                    {
                        lyricLine.Words.Add(new LyricWord
                        {
                            Word = ch,
                            StartMs = lineStartMs,
                            DurationMs = 0
                        });
                    }
                }
                return;
            }

            int count = tagMatches.Count;
            for (int i = 0; i < count; i++)
            {
                var match = tagMatches[i];
                long segStartMs = ParseQrcTimeToMs(
                    contentStr.AsSpan(match.Groups[1].Index, match.Groups[1].Length),
                    contentStr.AsSpan(match.Groups[2].Index, match.Groups[2].Length),
                    contentStr.AsSpan(match.Groups[3].Index, match.Groups[3].Length));

                int textStart = match.Index + match.Length;
                int textEnd = (i + 1 < count) ? tagMatches[i + 1].Index : contentStr.Length;
                var textSpan = contentStr.AsSpan(textStart, textEnd - textStart);
                if (textSpan.IsEmpty) continue;

                long segEndMs = (i + 1 < count)
                    ? ParseQrcTimeToMs(
                        contentStr.AsSpan(tagMatches[i + 1].Groups[1].Index, tagMatches[i + 1].Groups[1].Length),
                        contentStr.AsSpan(tagMatches[i + 1].Groups[2].Index, tagMatches[i + 1].Groups[2].Length),
                        contentStr.AsSpan(tagMatches[i + 1].Groups[3].Index, tagMatches[i + 1].Groups[3].Length))
                    : segStartMs;

                long segDurationMs = Math.Max(0, segEndMs - segStartMs);
                var subWords = SplitSpan(textSpan);
                int wordCount = subWords.Count;
                if (wordCount == 0) continue;

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
        public async Task<List<LyricLine>> ParseLrcLyrics(Music music, string? lrcContent, string? transLrcStr = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                lrcContent = music.Lyrics;
                transLrcStr = string.IsNullOrWhiteSpace(transLrcStr) ? music.TranslatedLyrics : transLrcStr;

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
                            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                            {
                                music.Lyrics = lyric;
                                music.TranslatedLyrics = trans;
                            });
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
                    return parsed;
            }

            return [new LyricLine { StartMs = 0, IsCurrent = true }];
        }

        // ──────────────────────────────────────────────────────────────
        //  通用工具方法
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 分詞：靜態預編譯 Regex，直接填 List 避免 LINQ + boxing。
        /// </summary>
        public static List<string> SplitEverything(string input)
        {
            if (string.IsNullOrEmpty(input)) return [];
            var result = new List<string>();
            foreach (Match m in s_splitPattern.Matches(input))
                result.Add(m.Value);
            return result;
        }

        /// <summary>
        /// 從 ReadOnlySpan 分詞，中間過程無額外分配（Regex 需要 string 時做一次 ToString）
        /// </summary>
        private static List<string> SplitSpan(ReadOnlySpan<char> input)
        {
            if (input.IsEmpty) return [];
            return SplitEverything(input.ToString());
        }

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

            int lyricCount = lyrics.Count;
            for (int i = 0; i < lyricCount; i++)
            {
                var currentLine = lyrics[i];

                currentLine.EndMs = (i < lyricCount - 1)
                    ? lyrics[i + 1].StartMs
                    : currentLine.StartMs + 5000.0;

                double rawMs = currentLine.EndMs - currentLine.StartMs;
                double reducedMs = Math.Max(0, rawMs - 200);
                int wordCount = currentLine.Words.Count;

                if (wordCount > 0)
                {
                    double perWordMs = reducedMs / wordCount;
                    var lineMs = currentLine.StartMs;
                    for (int j = 0; j < wordCount; j++)
                    {
                        currentLine.Words[j].StartMs = lineMs + perWordMs * j;
                        currentLine.Words[j].DurationMs = perWordMs;
                    }
                }
            }

            return lyrics;
        }

        /// <summary>
        /// LRC 時間行解析：Span 行迭代 + ValueSpan 直接解析，避免 Split string[] 和 Group.Value substring 分配
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

                string lineStr = trimmed.ToString();
                var timeMatch = s_lrcTimePattern.Match(lineStr);
                if (!timeMatch.Success) continue;

                var textSpan = lineStr.AsSpan(timeMatch.Length).Trim();
                // 過濾空行和僅含 "//" 的行
                if (textSpan.IsEmpty) continue;
                if (textSpan.Length == 2 && textSpan[0] == '/' && textSpan[1] == '/') continue;

                // ValueSpan 直接解析，無 substring 分配
                if (!int.TryParse(timeMatch.Groups[1].ValueSpan, out int minutes)) continue;
                if (!int.TryParse(timeMatch.Groups[2].ValueSpan, out int seconds)) continue;

                var msSpan = timeMatch.Groups[4].ValueSpan;
                if (!int.TryParse(msSpan, out int msRaw)) continue;
                int milliseconds = msSpan.Length == 2 ? msRaw * 10 : msRaw;

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