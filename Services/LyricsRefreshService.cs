using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private MusicDatabaseService _musicDatabaseService { get; }

        public LyricsRefreshService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;
        }

        public async Task<List<LyricLine>> SetLyrics(Music music)
        {
            // 1. 立即取消之前的任務
            CancelPreviousLyricsTask();
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var ct = _lyricsCancellationTokenSource.Token;

            try
            {
                // 2. 防抖：等待 500ms
                await Task.Delay(500, ct);

                // 3. 優先嘗試讀取本地歌詞文件（.krc / .qrc / .lrc）
                var localLyrics = TryParseLocalLyricsFile(music, ct);
                if (localLyrics is { Count: > 0 })
                {
                    music.PlayCount++;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    return localLyrics;
                }

                // 4. 降級：從 music.Krc 緩存解析（KRC/QRC 在線緩存或已存儲）
                var krcLyrics = await TryParseKrcLyrics(music, ct);
                if (krcLyrics.Count > 0)
                {
                    music.PlayCount++;
                    await _musicDatabaseService.UpdateMusicInfo(music);
                    return krcLyrics;
                }

                // 5. 再降級：走原有 LRC 流程（music.Lyrics 緩存或在線搜索）
                var (lrcContent, transLrcStr) = GetLyricsContentFromLrc(music);
                var lyrics = await ParseLrcLyrics(music, lrcContent, transLrcStr, ct);

                ct.ThrowIfCancellationRequested();
                music.PlayCount++;
                await _musicDatabaseService.UpdateMusicInfo(music);
                return lyrics;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }

        // ──────────────────────────────────────────────────────────────
        //  本地文件讀取
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 嘗試讀取本地歌詞文件，按 .krc → .qrc → .lrc 順序查找。
        /// 根據文件內容格式自動選擇解析方法，找不到或解析失敗均返回 null。
        /// </summary>
        private List<LyricLine>? TryParseLocalLyricsFile(Music music, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(music.Path)) return null;

            // 精確歌詞格式優先
            var candidates = new[] { ".krc", ".qrc", ".lrc" };

            foreach (var ext in candidates)
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
                catch { /* 文件讀取失敗，繼續嘗試下一個擴展名 */ }
            }

            return null;
        }

        /// <summary>
        /// 嘗試讀取對應的翻譯文件，格式：原文件名_Translated{ext}
        /// </summary>
        private static string? TryReadTranslationFile(string musicPath, string ext)
        {
            try
            {
                string fileName = Path.GetFileNameWithoutExtension(musicPath);
                string? dir = Path.GetDirectoryName(musicPath);
                if (string.IsNullOrEmpty(dir)) return null;

                string transPath = Path.Combine(dir, $"{fileName}_Translated{ext}");
                return File.Exists(transPath) ? File.ReadAllText(transPath) : null;
            }
            catch { return null; }
        }

        // ──────────────────────────────────────────────────────────────
        //  格式判斷與分發
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 根據內容格式自動選擇解析方法：QRC / KRC / LRC
        /// </summary>
        private List<LyricLine>? ParseByFormat(string content, string? transContent, CancellationToken ct)
        {
            List<LyricLine> lyrics;

            if (IsQrcFormat(content))
            {
                lyrics = ParseQrcLyrics(content, ct);
            }
            else if (IsKrcFormat(content))
            {
                lyrics = ParseKrcLyrics(content, ct);
            }
            else
            {
                // LRC 格式走原有流程
                return SpliteContent(content, transContent, []);
            }

            // KRC / QRC 的翻譯合併
            if (lyrics.Count > 0 && !string.IsNullOrWhiteSpace(transContent))
                MergeTranslation(lyrics, transContent);

            return lyrics;
        }

        /// <summary>
        /// 判斷是否為 QRC 格式：行內含有 &lt;mm:ss.xx&gt; 形式的字級時間標籤
        /// </summary>
        private static bool IsQrcFormat(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            return Regex.IsMatch(content, @"<\d{2}:\d{2}\.\d{2,3}>");
        }

        /// <summary>
        /// 判斷是否為 KRC 格式：行首為 [數字,數字]
        /// </summary>
        private static bool IsKrcFormat(string content)
        {
            if (string.IsNullOrWhiteSpace(content)) return false;
            return Regex.IsMatch(content, @"^\[\d+,\d+\]", RegexOptions.Multiline);
        }

        // ──────────────────────────────────────────────────────────────
        //  KRC 解析
        // ──────────────────────────────────────────────────────────────

        private async Task<List<LyricLine>> TryParseKrcLyrics(Music music, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(music.Krc) && AppSettings.IsAutoLyricsEnabled && !music.IsKrcSearched)
            {
                try
                {
                    var (krc, tkrc) = await App.Services.GetRequiredService<LrcService>().GetKrcLyricsAsync(
                        music, cancellationToken);
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
        /// 解析 KRC 格式：[startMs,durationMs]字(offsetMs,durationMs)...
        /// </summary>
        private List<LyricLine> ParseKrcLyrics(string krc, CancellationToken cancellationToken = default)
        {
            var lyrics = new List<LyricLine>();
            var linePattern = new Regex(@"^\[(\d+),(\d+)\](.*)$");

            string[] lines = krc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                if (trimmed.StartsWith("[ti:") || trimmed.StartsWith("[ar:") ||
                    trimmed.StartsWith("[al:") || trimmed.StartsWith("[by:") ||
                    trimmed.StartsWith("[offset:") || trimmed.StartsWith("[kana:"))
                    continue;

                var lineMatch = linePattern.Match(trimmed);
                if (!lineMatch.Success) continue;

                long lineStartMs = long.Parse(lineMatch.Groups[1].Value);
                string content = lineMatch.Groups[3].Value;

                if (string.IsNullOrWhiteSpace(content)) continue;

                var lyricLine = new LyricLine
                {
                    Time = TimeSpan.FromMilliseconds(lineStartMs),
                    IsCurrent = false
                };

                ParseKrcWords(content, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            return lyrics;
        }

        private void ParseKrcWords(string content, long lineStartMs, LyricLine lyricLine)
        {
            int i = 0;
            while (i < content.Length)
            {
                int parenOpen = content.IndexOf('(', i);
                if (parenOpen == -1)
                {
                    var remaining = content[i..];
                    foreach (var ch in SplitEverything(remaining))
                    {
                        lyricLine.Words.Add(new LyricWord
                        {
                            Word = ch,
                            StartTime = TimeSpan.FromMilliseconds(lineStartMs),
                            Duration = TimeSpan.Zero
                        });
                    }
                    break;
                }

                string wordText = content[i..parenOpen];
                int parenClose = content.IndexOf(')', parenOpen);
                if (parenClose == -1) break;

                string timeStr = content[(parenOpen + 1)..parenClose];
                var parts = timeStr.Split(',');
                if (parts.Length == 2 &&
                    long.TryParse(parts[0], out long offsetMs) &&
                    long.TryParse(parts[1], out long durationMs))
                {
                    if (!string.IsNullOrEmpty(wordText))
                    {
                        var subWords = SplitEverything(wordText);
                        double perMs = subWords.Count > 0 ? (double)durationMs / subWords.Count : durationMs;
                        for (int k = 0; k < subWords.Count; k++)
                        {
                            lyricLine.Words.Add(new LyricWord
                            {
                                Word = subWords[k],
                                StartTime = TimeSpan.FromMilliseconds(offsetMs + perMs * k),
                                Duration = TimeSpan.FromMilliseconds(perMs)
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
        /// 解析 QRC 格式：[mm:ss.xx]&lt;mm:ss.xx&gt;字&lt;mm:ss.xx&gt;字...
        /// 每個 &lt;time&gt; 是緊接其後那個字的開始時間，字的時長由下一個時間標籤推算
        /// </summary>
        private List<LyricLine> ParseQrcLyrics(string qrc, CancellationToken cancellationToken = default)
        {
            var lyrics = new List<LyricLine>();

            // 行時間標籤：[mm:ss.xx] 或 [mm:ss.xxx]
            var lineTimePattern = new Regex(@"^\[(\d{2}):(\d{2})\.(\d{2,3})\](.*)$");
            // 字級時間標籤：<mm:ss.xx>
            var wordTimePattern = new Regex(@"<(\d{2}):(\d{2})\.(\d{2,3})>");

            string[] lines = qrc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            cancellationToken.ThrowIfCancellationRequested();

            foreach (var rawLine in lines)
            {
                string trimmed = rawLine.Trim();

                // 跳過元數據標籤
                if (trimmed.StartsWith("[ti:") || trimmed.StartsWith("[ar:") ||
                    trimmed.StartsWith("[al:") || trimmed.StartsWith("[by:") ||
                    trimmed.StartsWith("[offset:") || trimmed.StartsWith("[kana:"))
                    continue;

                var lineMatch = lineTimePattern.Match(trimmed);
                if (!lineMatch.Success) continue;

                long lineStartMs = ParseQrcTimeToMs(
                    lineMatch.Groups[1].Value,
                    lineMatch.Groups[2].Value,
                    lineMatch.Groups[3].Value);

                string content = lineMatch.Groups[4].Value;
                if (string.IsNullOrWhiteSpace(content)) continue;

                var lyricLine = new LyricLine
                {
                    Time = TimeSpan.FromMilliseconds(lineStartMs),
                    IsCurrent = false
                };

                ParseQrcWords(content, lineStartMs, wordTimePattern, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            return lyrics;
        }

        /// <summary>
        /// 解析 QRC 字級內容：&lt;t0&gt;字A&lt;t1&gt;字B&lt;t2&gt;...
        /// 字的 StartTime = t_n，Duration = t_{n+1} - t_n（最後一字 Duration = 0）
        /// </summary>
        private void ParseQrcWords(string content, long lineStartMs, Regex wordTimePattern, LyricLine lyricLine)
        {
            var tagMatches = wordTimePattern.Matches(content);
            if (tagMatches.Count == 0)
            {
                // 沒有字級標籤，整行作為一組詞
                var text = wordTimePattern.Replace(content, "").Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    foreach (var ch in SplitEverything(text))
                    {
                        lyricLine.Words.Add(new LyricWord
                        {
                            Word = ch,
                            StartTime = TimeSpan.FromMilliseconds(lineStartMs),
                            Duration = TimeSpan.Zero
                        });
                    }
                }
                return;
            }

            // 建立 (timeMs, textAfterTag) 對，每個 tag 後的文字屬於該 tag
            var segments = new List<(long StartMs, string Text)>();

            for (int i = 0; i < tagMatches.Count; i++)
            {
                var match = tagMatches[i];
                long startMs = ParseQrcTimeToMs(
                    match.Groups[1].Value,
                    match.Groups[2].Value,
                    match.Groups[3].Value);

                int textStart = match.Index + match.Length;
                int textEnd = (i + 1 < tagMatches.Count) ? tagMatches[i + 1].Index : content.Length;
                string text = content[textStart..textEnd];

                if (!string.IsNullOrEmpty(text))
                    segments.Add((startMs, text));
            }

            // 根據相鄰 segment 計算每段時長，再按字均分
            for (int i = 0; i < segments.Count; i++)
            {
                var (segStartMs, segText) = segments[i];
                long segEndMs = (i + 1 < segments.Count) ? segments[i + 1].StartMs : segStartMs;
                long segDurationMs = Math.Max(0, segEndMs - segStartMs);

                var subWords = SplitEverything(segText);
                if (subWords.Count == 0) continue;

                double perMs = segDurationMs > 0 ? (double)segDurationMs / subWords.Count : 0;

                for (int k = 0; k < subWords.Count; k++)
                {
                    lyricLine.Words.Add(new LyricWord
                    {
                        Word = subWords[k],
                        StartTime = TimeSpan.FromMilliseconds(segStartMs + perMs * k),
                        Duration = TimeSpan.FromMilliseconds(perMs)
                    });
                }
            }
        }

        /// <summary>
        /// 將 QRC 時間字串（mm, ss, ms字串）轉為毫秒
        /// </summary>
        private static long ParseQrcTimeToMs(string mm, string ss, string msStr)
        {
            int minutes = int.Parse(mm);
            int seconds = int.Parse(ss);
            // 2位數 → ×10，3位數直接用
            int milliseconds = msStr.Length == 2
                ? int.Parse(msStr) * 10
                : int.Parse(msStr);
            return minutes * 60000L + seconds * 1000L + milliseconds;
        }

        // ──────────────────────────────────────────────────────────────
        //  翻譯合併（KRC/QRC 共用）
        // ──────────────────────────────────────────────────────────────

        /// <summary>
        /// 將翻譯文本合併到已解析的歌詞行（KRC/QRC 共用）
        /// </summary>
        private void MergeTranslation(List<LyricLine> lyrics, string transContent)
        {
            ParseLrcToLines(transContent, (time, transText) =>
            {
                var bestMatch = lyrics
                    .Select(l => new { Line = l, Diff = Math.Abs((l.Time - time).TotalMilliseconds) })
                    .Where(x => x.Diff <= 100)
                    .OrderBy(x => x.Diff)
                    .FirstOrDefault();

                bestMatch?.Line.TransLateText = transText;
            });
        }

        // ──────────────────────────────────────────────────────────────
        //  LRC 解析
        // ──────────────────────────────────────────────────────────────

        private static (string?, string?) GetLyricsContentFromLrc(Music music, string extension = ".lrc")
        {
            string? lrcContent = null;
            string? transLrcStr = null;

            if (!string.IsNullOrWhiteSpace(music.Path))
            {
                // 讀取主歌詞文件
                try
                {
                    string lyricFilePath = Path.ChangeExtension(music.Path, extension);
                    if (File.Exists(lyricFilePath))
                        lrcContent = File.ReadAllText(lyricFilePath);
                }
                catch { lrcContent = null; }

                // 讀取翻譯歌詞文件
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(music.Path);
                    string transFileName = $"{fileName}_Translated{extension}";
                    string? directoryPath = Path.GetDirectoryName(music.Path);

                    if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                    {
                        string transFilePath = Path.Combine(directoryPath, transFileName);
                        if (File.Exists(transFilePath))
                            transLrcStr = File.ReadAllText(transFilePath);
                    }
                }
                catch { transLrcStr = null; }
            }

            return (lrcContent, transLrcStr);
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(Music music, string? lrcContent, string? transLrcStr = null, CancellationToken cancellationToken = default)
        {
            List<LyricLine> lyrics = [];

            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                // 嘗試從內存緩存獲取
                lrcContent = music.Lyrics;
                transLrcStr = string.IsNullOrWhiteSpace(transLrcStr) ? music.TranslatedLyrics : transLrcStr;

                // 如果開啟了自動歌詞且緩存為空且未搜索過，則在線搜索
                if (string.IsNullOrWhiteSpace(lrcContent) && AppSettings.IsAutoLyricsEnabled && !music.IsLrcSearched)
                {
                    try
                    {
                        var (lyric, trans) = await App.Services.GetRequiredService<LrcService>().GetMixedLyricsAsync(
                            music, cancellationToken);
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
                    catch (OperationCanceledException) { Debug.WriteLine("歌詞任務取消"); }
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!string.IsNullOrWhiteSpace(lrcContent))
                return SpliteContent(lrcContent, transLrcStr, lyrics);

            // 無歌詞時的默認佔位
            lyrics.Add(new LyricLine
            {
                Time = TimeSpan.Zero,
                IsCurrent = true
            });
            return lyrics;
        }

        // ──────────────────────────────────────────────────────────────
        //  通用工具方法
        // ──────────────────────────────────────────────────────────────

        public static List<string> SplitEverything(string input)
        {
            if (string.IsNullOrEmpty(input)) return new List<string>();

            // 1. [\u4e00-\u9fa5] : 中文字符（單字）
            // 2. [\u3040-\u30ff] : 日文字符（單字）
            // 3. [\p{L}\p{N}]+   : 表音文字單詞（連續字母或數字）
            // 4. \s+             : 連續空格
            // 5. .               : 任何其他單個字符（標點等）
            string pattern = @"([\u4e00-\u9fa5]|[\u3040-\u30ff]|[\p{L}\p{N}]+|\s+|.)";

            return Regex.Matches(input, pattern)
                        .Cast<Match>()
                        .Select(m => m.Value)
                        .ToList();
        }

        private List<LyricLine> SpliteContent(string lrcContent, string? transLrc, List<LyricLine> lyrics)
        {
            lyrics.Clear();

            // 1. 解析原文
            ParseLrcToLines(lrcContent, (time, text) =>
            {
                var line = new LyricLine { Time = time, IsCurrent = false };
                var wordStrings = SplitEverything(text);
                foreach (var w in wordStrings)
                    line.Words.Add(new LyricWord { Word = w });
                lyrics.Add(line);
            });

            // 2. 解析翻譯
            if (!string.IsNullOrEmpty(transLrc))
            {
                ParseLrcToLines(transLrc, (time, transText) =>
                {
                    var lyric = lyrics.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) <= 50);
                    if (lyric != null) lyric.TransLateText = transText;
                });
            }

            // 3. 排序
            var sortedLyrics = lyrics.OrderBy(l => l.Time).ToList();

            // 4. 計算行時長及單詞時長
            for (int i = 0; i < sortedLyrics.Count; i++)
            {
                var currentLine = sortedLyrics[i];

                TimeSpan rawDuration = (i < sortedLyrics.Count - 1)
                    ? sortedLyrics[i + 1].Time - currentLine.Time
                    : TimeSpan.FromSeconds(5);

                // 減去 200ms，確保時長不小於 0
                double reducedMs = Math.Max(0, rawDuration.TotalMilliseconds - 200);
                TimeSpan lineDuration = TimeSpan.FromMilliseconds(reducedMs);

                if (currentLine.Words.Count > 0)
                {
                    double perWordMs = lineDuration.TotalMilliseconds / currentLine.Words.Count;
                    for (int j = 0; j < currentLine.Words.Count; j++)
                    {
                        currentLine.Words[j].StartTime = currentLine.Time + TimeSpan.FromMilliseconds(perWordMs * j);
                        currentLine.Words[j].Duration = TimeSpan.FromMilliseconds(perWordMs);
                    }
                }
            }

            return sortedLyrics;
        }

        /// <summary>
        /// 核心解析邏輯：處理 LRC 時間標籤並提取文本
        /// </summary>
        private void ParseLrcToLines(string content, Action<TimeSpan, string> onLineParsed)
        {
            if (string.IsNullOrEmpty(content)) return;

            string[] lines = content.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            const string TimeTagPattern = @"\[(\d{2}):(\d{2})([.:])(\d{2,3})\]";

            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || !trimmedLine.StartsWith("["))
                    continue;

                Match timeMatch = Regex.Match(trimmedLine, TimeTagPattern);
                if (!timeMatch.Success) continue;

                string text = trimmedLine[timeMatch.Length..].Trim();

                // 過濾空行和僅含 "//" 的行
                if (string.IsNullOrWhiteSpace(text) || text.Equals("//"))
                    continue;

                int minutes = int.Parse(timeMatch.Groups[1].Value);
                int seconds = int.Parse(timeMatch.Groups[2].Value);
                string millisecondStr = timeMatch.Groups[4].Value;

                int milliseconds = millisecondStr.Length == 2
                    ? int.Parse(millisecondStr) * 10
                    : int.Parse(millisecondStr);

                TimeSpan time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                onLineParsed(time, text);
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