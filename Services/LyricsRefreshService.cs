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
        public List<LyricLine> Lyrics
        {
            get => field;
            set => field = value;
        } = [];
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private MusicDatabaseService _musicDatabaseService { get; }
        private AppViewModel AppViewModel { get; }
        public LyricsRefreshService(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            _musicDatabaseService = musicDatabaseService;
        }
        //public void UpdateLyrics(TimeSpan currentPosition)
        //{
        //    if (Lyrics.Count == 0)
        //        return;
        //    // 查找当前应显示的歌词
        //    int currentIndex = -1;
        //    for (int i = 0; i < Lyrics.Count; i++)
        //    {
        //        // 找到时间戳小于等于当前播放位置的最后一条歌词
        //        if (Lyrics[i].Time <= currentPosition)
        //        {
        //            currentIndex = i;
        //        }
        //        else
        //        {
        //            break;
        //        }
        //    }
        //    // 触发事件通知UI更新
        //    if (currentIndex >= 0)
        //    {
        //        AppViewModel.UpdateLyricsToUI(currentIndex);
        //    }
        //}

        public void ResetLyrics()
        {
            if (Lyrics.Count == 0)
                return;
            AppViewModel.UpdateLyricsToUI(0);
        }

        public async Task SetLyrics(Music music)
        {
            CancelPreviousLyricsTask();
            Lyrics.Clear();
            var (lrcContent,transLrcStr) = GetLyricsContentFromLrc(music.Path);
            var lyricsContent = await ParseLrcLyrics(lrcContent, transLrcStr);
            if (lyricsContent is not null)
            {
                Lyrics = lyricsContent;
            }
        }

        private (string?,string?) GetLyricsContentFromLrc(string? path)
        {
            string? lrcContent = null;
            string? transLrcStr = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    string lyricFilePath = Path.ChangeExtension(path, ".lrc");
                    if (File.Exists(lyricFilePath))
                    {
                        lrcContent = File.ReadAllText(lyricFilePath);
                    }                        
                }
                catch (Exception)
                {
                    lrcContent = null;
                }

                try
                {
                    string fileName = Path.GetFileNameWithoutExtension(path);
                    string transFileName = $"{fileName}_Translated.lrc";
                    string? directoryPath = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directoryPath) && Directory.Exists(directoryPath))
                    {
                        string lrcFilePath = Path.Combine(directoryPath, transFileName);
                        if (File.Exists(lrcFilePath))
                        {
                            transLrcStr = File.ReadAllText(lrcFilePath);
                        }
                    }
                }
                catch {
                    transLrcStr = null;
                }                
            }
            return (lrcContent, transLrcStr);
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(string? lrcContent,string? transLrcStr = null)
        {
            _lyricsCancellationTokenSource?.Cancel(); // 习惯性清理旧任务
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _lyricsCancellationTokenSource.Token;

            var currentMusic = AppViewModel.CurrentPlayingMusic;
            if (currentMusic is null) return [];

            List<LyricLine> lyrics = [];

            // 1. 始终增加播放计数 (内存中)
            currentMusic.PlayCount++;
            //var songInMemory = AppViewModel.SongsSource.AsValueEnumerable().FirstOrDefault(m => m.Id == currentMusic.Id);
            //songInMemory?.PlayCount = currentMusic.PlayCount;

            // 2. 确定歌词内容
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                // 尝试从内存缓存获取
                lrcContent = currentMusic.Lyrics;
                transLrcStr = string.IsNullOrWhiteSpace(transLrcStr) ? currentMusic.TranslatedLyrics:transLrcStr;
                // 如果开启了自动歌词且缓存为空，则在线搜索
                if (string.IsNullOrWhiteSpace(lrcContent) && AppSettings.IsAutoLyricsEnabled)
                {
                    try
                    {
                        var (lyric, trans) = await App.Services.GetRequiredService<LrcService>().GetMixedLyricsAsync(
                            currentMusic,cancellationToken);
                        if (!string.IsNullOrEmpty(lyric))
                        {
                            lrcContent = lyric;
                            transLrcStr = trans;
                            currentMusic.Lyrics = lyric;
                            currentMusic.TranslatedLyrics = trans;
                        }
                    }
                    catch (OperationCanceledException) { Debug.WriteLine("歌词任务取消"); }
                }
            }

            // 3. 统一执行一次数据库 IO
            await _musicDatabaseService.UpdateMusicInfo(currentMusic);

            // 4. 返回解析结果
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
                TimeSpan lineDuration = (i < sortedLyrics.Count - 1)
                    ? sortedLyrics[i + 1].Time - currentLine.Time
                    : TimeSpan.FromSeconds(5);

                // 计算单词时间：平均分配
                if (currentLine.Words.Count > 0)
                {
                    double perWordMs = lineDuration.TotalMilliseconds / currentLine.Words.Count;
                    for (int j = 0; j < currentLine.Words.Count; j++)
                    {
                        // StartTime 必须是绝对时间：行开始时间 + 单词偏移
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
                    int minutes = int.Parse(timeMatch.Groups[1].Value);
                    int seconds = int.Parse(timeMatch.Groups[2].Value);
                    string millisecondStr = timeMatch.Groups[4].Value;

                    int milliseconds = millisecondStr.Length == 2
                        ? int.Parse(millisecondStr) * 10
                        : int.Parse(millisecondStr);

                    TimeSpan time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
                    string text = trimmedLine.Substring(timeMatch.Length).Trim();

                    if (!string.IsNullOrEmpty(text))
                    {
                        onLineParsed(time, text);
                    }
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
