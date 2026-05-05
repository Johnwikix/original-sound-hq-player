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
    public class LyricsRefreshService: IDisposable
    {
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private MusicDatabaseService _musicDatabaseService { get; }
        public LyricsRefreshService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            _musicDatabaseService = musicDatabaseService;
        }

        public async Task<List<LyricLine>> SetLyrics(Music music)
        {
            // 1. 立即取消之前的任务
            CancelPreviousLyricsTask();
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var ct = _lyricsCancellationTokenSource.Token;

            try
            {
                // 2. 防抖：等待 500ms
                // 如果在 500ms 内再次调用了 SetLyrics，上一个任务会被 CancelPreviousLyricsTask 取消
                // 从而这里的 Task.Delay 会抛出 OperationCanceledException 直接中断任务
                await Task.Delay(500, ct);

                // 3. 执行真正的逻辑
                // 优先尝试解析KRC精确歌词
                var lyrics = await TryParseKrcLyrics(music, ct);
                if (lyrics.Count == 0)
                {
                    // 4. 降级：走原有LRC流程
                    var (lrcContent, transLrcStr) = GetLyricsContentFromLrc(music);
                    lyrics = await ParseLrcLyrics(music, lrcContent, transLrcStr, ct);

                    // 检查一次取消，避免无效 IO
                    ct.ThrowIfCancellationRequested();
                }                

                music.PlayCount++;
                await _musicDatabaseService.UpdateMusicInfo(music);
                return lyrics;
            }
            catch (OperationCanceledException)
            {
                return [];
            }
        }

        private async Task<List<LyricLine>> TryParseKrcLyrics(Music music, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(music.Krc) && AppSettings.IsAutoLyricsEnabled && !music.IsLrcSearched)
            {
                try
                {
                    var (krc, tkrc) = await App.Services.GetRequiredService<LrcService>().GetKrcLyricsAsync(
                        music, cancellationToken);
                    if (!string.IsNullOrEmpty(krc))
                    {
                        await App.MainWindow.DispatcherQueue.EnqueueAsync(() => {
                            music.Krc = krc;
                            music.TKrc = tkrc;
                        });                                              
                    }
                    music.IsKrcSearched = true;
                }
                catch (OperationCanceledException) {}
            }

            var lyrics = new List<LyricLine>();

            // 解析KRC行，格式: [startMs,durationMs]字(offsetMs,durationMs)...
            var linePattern = new Regex(@"^\[(\d+),(\d+)\](.*)$");
            // 字级别时间标签，格式: 字(offsetMs,durationMs)
            var wordPattern = new Regex(@"([^\x00-\x7F]|[\w\p{P}]+)\((\d+),(\d+)\)|([^\(（]+?)(?=\(|\[|$)");

            string[] lines = music.Krc.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var rawLine in lines)
            {
                string trimmed = rawLine.Trim();
                // 跳过头部元数据和kana标签
                if (trimmed.StartsWith("[ti:") || trimmed.StartsWith("[ar:") ||
                    trimmed.StartsWith("[al:") || trimmed.StartsWith("[by:") ||
                    trimmed.StartsWith("[offset:") || trimmed.StartsWith("[kana:"))
                    continue;

                var lineMatch = linePattern.Match(trimmed);
                if (!lineMatch.Success) continue;

                long lineStartMs = long.Parse(lineMatch.Groups[1].Value);
                // long lineDurationMs = long.Parse(lineMatch.Groups[2].Value); // 备用
                string content = lineMatch.Groups[3].Value;

                if (string.IsNullOrWhiteSpace(content)) continue;

                var lyricLine = new LyricLine
                {
                    Time = TimeSpan.FromMilliseconds(lineStartMs),
                    IsCurrent = false
                };

                // 解析字级别时间：格式 字(offsetMs,durationMs)
                // 用更直接的手动解析处理混合字符
                ParseKrcWords(content, lineStartMs, lyricLine);

                if (lyricLine.Words.Count > 0)
                    lyrics.Add(lyricLine);
            }

            if (lyrics.Count == 0) return [];

            if (!string.IsNullOrWhiteSpace(music.TKrc))
            {
                ParseLrcToLines(music.TKrc, (time, transText) =>
                {
                    // 方案：寻找时间差最小的那一行，而不是第一行
                    var bestMatch = lyrics
                        .Select(l => new { Line = l, Diff = Math.Abs((l.Time - time).TotalMilliseconds) })
                        .Where(x => x.Diff <= 100) // 容差可以稍微放大，但后面用 OrderBy 兜底
                        .OrderBy(x => x.Diff)      // 核心：按距离排序，取最接近的
                        .FirstOrDefault();

                    bestMatch?.Line.TransLateText = transText;
                });
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
                            StartTime = TimeSpan.FromMilliseconds(lineStartMs), // 无时间标签时用行时间
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


        private static (string?, string?) GetLyricsContentFromLrc(Music music, string extension= ".lrc")
        {
            string? lrcContent = null;
            string? transLrcStr = null;

            if (!string.IsNullOrWhiteSpace(music.Path))
            {
                // 读取主歌词文件（.lrc 或 .krc）
                try
                {
                    string lyricFilePath = Path.ChangeExtension(music.Path, extension);
                    if (File.Exists(lyricFilePath))
                    {
                        lrcContent = File.ReadAllText(lyricFilePath);
                    }
                }
                catch
                {
                    lrcContent = null;
                }

                // 读取翻译歌词文件（原文件名_Translated.lrc 或 .krc）
                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(music.Path);
                    string transFileName = $"{fileName}_Translated{extension}";
                    string? directoryPath = Path.GetDirectoryName(music.Path);

                    if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                    {
                        string transFilePath = Path.Combine(directoryPath, transFileName);
                        if (File.Exists(transFilePath))
                        {
                            transLrcStr = File.ReadAllText(transFilePath);
                        }
                    }
                }
                catch
                {
                    transLrcStr = null;
                }
            }

            return (lrcContent, transLrcStr);
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(Music music,string? lrcContent,string? transLrcStr = null,CancellationToken cancellationToken = default)
        {
            List<LyricLine> lyrics = [];           
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                // 尝试从内存缓存获取
                lrcContent = music.Lyrics;
                transLrcStr = string.IsNullOrWhiteSpace(transLrcStr) ? music.TranslatedLyrics:transLrcStr;
                // 如果开启了自动歌词且缓存为空并且未搜索过，则在线搜索
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
                            await App.MainWindow.DispatcherQueue.EnqueueAsync(() => {
                                music.Lyrics = lyric;
                                music.TranslatedLyrics = trans;
                            });                                                    
                        }
                        music.IsLrcSearched = true;
                    }
                    catch (OperationCanceledException) { Debug.WriteLine("歌词任务取消"); }
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                return SpliteContent(lrcContent,transLrcStr,lyrics);
            }

            // 无歌词时的默认占位
            lyrics.Add(new LyricLine
            {
                //Text = ToolUtils.GetString("LyricsGetFailed"),
                Time = TimeSpan.Zero,
                IsCurrent = true
            });
            return lyrics;
        }

        public static List<string> SplitEverything(string input)
        {
            if (string.IsNullOrEmpty(input)) return new List<string>();

            // 逻辑解释：
            // 1. [\u4e00-\u9fa5] : 匹配中文字符（单字）
            // 2. [\u3040-\u30ff] : 匹配日文字符（单字）
            // 3. [\p{L}\p{N}]+   : 匹配表音文字单词（连续的字母或数字，涵盖德法俄希等）
            // 4. \s+             : 匹配连续的空格
            // 5. .               : 匹配任何其他单个字符（包括所有中西文标点）

            string pattern = @"([\u4e00-\u9fa5]|[\u3040-\u30ff]|[\p{L}\p{N}]+|\s+|.)";

            return Regex.Matches(input, pattern)
                        .Cast<Match>()
                        .Select(m => m.Value)
                        .ToList();
        }

        private void CancelPreviousLyricsTask()
        {
            if (_lyricsCancellationTokenSource is not null)
            {
                try
                {
                    if (!_lyricsCancellationTokenSource.IsCancellationRequested)
                    {
                        _lyricsCancellationTokenSource.Cancel();
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 忽略已释放对象的异常
                }
                finally
                {
                    _lyricsCancellationTokenSource.Dispose();
                    _lyricsCancellationTokenSource = null;
                }
            }
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
                {
                    line.Words.Add(new LyricWord { Word = w });
                }
                lyrics.Add(line);
            });

            // 2. 解析翻译 (保持不变)
            if (!string.IsNullOrEmpty(transLrc))
            {
                ParseLrcToLines(transLrc, (time, transText) => {
                    var lyric = lyrics.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) <= 50);
                    if (lyric != null) lyric.TransLateText = transText;
                });
            }

            // 3. 排序
            var sortedLyrics = lyrics.OrderBy(l => l.Time).ToList();

            // 4. 计算行时长及单词时长 (核心逻辑)
            for (int i = 0; i < sortedLyrics.Count; i++)
            {
                var currentLine = sortedLyrics[i];

                // 获取原始行间隔
                TimeSpan rawDuration = (i < sortedLyrics.Count - 1)
                    ? sortedLyrics[i + 1].Time - currentLine.Time
                    : TimeSpan.FromSeconds(5);

                // --- 修改点：减去 200ms，并确保时长不小于 0 ---
                double reducedMs = Math.Max(0, rawDuration.TotalMilliseconds - 200);
                TimeSpan lineDuration = TimeSpan.FromMilliseconds(reducedMs);

                // 计算单词时间：平均分配
                if (currentLine.Words.Count > 0)
                {
                    // 使用减去 200ms 后的时长进行平分
                    double perWordMs = lineDuration.TotalMilliseconds / currentLine.Words.Count;

                    for (int j = 0; j < currentLine.Words.Count; j++)
                    {
                        // StartTime 保持绝对时间：行开始时间 + 单词偏移
                        currentLine.Words[j].StartTime = currentLine.Time + TimeSpan.FromMilliseconds(perWordMs * j);
                        currentLine.Words[j].Duration = TimeSpan.FromMilliseconds(perWordMs);
                    }
                }
            }
            return sortedLyrics;
        }

        /// <summary>
        /// 核心解析逻辑：处理时间标签并提取文本
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
                if (timeMatch.Success)
                {
                    // 提取时间戳后的文本内容
                    string text = trimmedLine[timeMatch.Length..].Trim();

                    // --- 新增：过滤逻辑 ---
                    // 1. 检查是否为空或仅为空白字符
                    // 2. 检查是否仅包含 "//"
                    if (string.IsNullOrWhiteSpace(text) || text.Equals("//"))
                    {
                        continue; 
                    }
                    // --------------------

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
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                CancelPreviousLyricsTask();
            }
        }
    }
}
