using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
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
        public void UpdateLyrics(TimeSpan currentPosition)
        {
            if (Lyrics.Count == 0)
                return;
            // 查找当前应显示的歌词
            int currentIndex = -1;
            for (int i = 0; i < Lyrics.Count; i++)
            {
                // 找到时间戳小于等于当前播放位置的最后一条歌词
                if (Lyrics[i].Time <= currentPosition)
                {
                    currentIndex = i;
                }
                else
                {
                    break;
                }
            }
            // 触发事件通知UI更新
            if (currentIndex >= 0)
            {
                AppViewModel.UpdateLyricsToUI(currentIndex);
            }
        }

        public void ResetLyrics()
        {
            if (Lyrics.Count == 0)
                return;
            AppViewModel.UpdateLyricsToUI(0);
        }

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            Lyrics.Clear();
            var (lrcContent,transLrcStr) = GetLyricsContentFromLrc(AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == AppViewModel.CurrentPlayingMusic?.Id)?.Path);
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
            //var songInMemory = AppViewModel.AllSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == currentMusic.Id);
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
                        var (lyric, trans) = await LrcService.GetLyricsAsync(
                            currentMusic.Title, currentMusic.Album, currentMusic.Author, cancellationToken);

                        if (!string.IsNullOrEmpty(lyric))
                        {
                            lrcContent = lyric;
                            transLrcStr = trans;
                            currentMusic.Lyrics = lyric;
                            currentMusic.TranslatedLyrics = trans;                            
                            //if (songInMemory != null)
                            //{
                            //    songInMemory.Lyrics = lyric;
                            //    songInMemory.TranslatedLyrics = trans;
                            //}

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
                Text = ToolUtils.GetString("LyricsGetFailed"),
                Time = TimeSpan.Zero,
                IsCurrent = true
            });
            return lyrics;
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

            ParseLrcToLines(lrcContent, (time, text) =>
            {
                // 查找是否已有时间相近的行
                var existingLine = lyrics.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) <= 50);

                if (existingLine != null)
                {
                    // 如果时间接近，合并文本（换行处理）
                    existingLine.Text += "\n" + text;
                }
                else
                {
                    // 如果是新时间点，添加新行
                    lyrics.Add(new LyricLine
                    {
                        Time = time,
                        Text = text,
                        IsCurrent = false
                    });
                }
            });

            // 2. 解析翻译歌词
            if (!string.IsNullOrEmpty(transLrc))
            {
                ParseLrcToLines(transLrc, (time, transText) =>
                {
                    // 匹配原文中时间最接近的行
                    var lyric = lyrics.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) <= 50);
                    // 赋值翻译文本（如果有多行翻译，也可以考虑用 += "\n" + transText）
                    lyric?.TransLateText = transText;
                });
            }

            // 3. 兜底处理：无歌词情况
            if (lyrics.Count == 0)
            {
                lyrics.Add(new LyricLine
                {
                    Text = ToolUtils.GetString("NoRecognizableLyrics"),
                    Time = TimeSpan.Zero,
                    IsCurrent = true
                });
                return lyrics;
            }

            // 4. 按时间排序（必须先排序，才能计算相邻行差值）
            var sortedLyrics = lyrics.OrderBy(l => l.Time).ToList();

            // 5. 计算 LineAnimateDuration
            for (int i = 0; i < sortedLyrics.Count; i++)
            {
                if (i < sortedLyrics.Count - 1)
                {
                    // 当前行持续时间 = 下一行时间 - 当前行时间
                    sortedLyrics[i].LineAnimateDuration = sortedLyrics[i + 1].Time - sortedLyrics[i].Time;
                }
                else
                {
                    // 最后一行，默认为 5s
                    sortedLyrics[i].LineAnimateDuration = TimeSpan.FromSeconds(5);
                }
            }

            // 4. 按时间排序返回
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
