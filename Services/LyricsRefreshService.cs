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
    public class LyricsRefreshService
    {
        public List<LyricLine> Lyrics
        {
            get => field;
            set => field = value;
        } = [];
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private MusicBrowseViewModel MusicBrowseViewModel { get; }
        public LyricsRefreshService()
        {
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
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
                MusicBrowseViewModel.UpdateLyricsToUI(currentIndex);
            }
        }

        public void ResetLyrics()
        {
            if (Lyrics.Count == 0)
                return;
            MusicBrowseViewModel.UpdateLyricsToUI(0);
        }

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            Lyrics.Clear();
            string? lrcContent = GetLyricsContentFromLrc(AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Path);
            var lyricsContent = await ParseLrcLyrics(lrcContent);
            if (lyricsContent is not null)
            {
                Lyrics = lyricsContent;
            }
        }

        private string GetLyricsContentFromLrc(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
            {
                string lyricFilePath = Path.ChangeExtension(path, ".lrc");
                if (File.Exists(lyricFilePath))
                {
                    try
                    {
                        return File.ReadAllText(lyricFilePath);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
            }
            return null;
        }

        public async Task<List<LyricLine>> ParseLrcLyrics(string? lrcContent)
        {
            string? transLrcStr = string.Empty;
            _lyricsCancellationTokenSource?.Cancel(); // 习惯性清理旧任务
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _lyricsCancellationTokenSource.Token;

            var currentMusic = MusicBrowseViewModel.CurrentPlayingMusic;
            if (currentMusic == null) return new List<LyricLine>();

            List<LyricLine> lyrics = new List<LyricLine>();
            bool needUpdateDb = false;

            // 1. 始终增加播放计数 (内存中)
            currentMusic.PlayCount++;
            var songInMemory = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == currentMusic.Id);
            if (songInMemory != null) songInMemory.PlayCount = currentMusic.PlayCount;
            needUpdateDb = true;

            // 2. 确定歌词内容
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                // 尝试从内存缓存获取
                lrcContent = songInMemory?.Lyrics;
                transLrcStr = songInMemory?.TranslatdeLyrics;
                // 如果开启了自动歌词且缓存为空，则在线搜索
                if (string.IsNullOrWhiteSpace(lrcContent) && AppSettings.isAutoLyricsEnabled)
                {
                    try
                    {
                        var (lyric, trans) = await LrcService.GetLyricsAsync(
                            currentMusic.Title, currentMusic.Album, currentMusic.Author, cancellationToken);

                        if (!string.IsNullOrEmpty(lyric))
                        {
                            lrcContent = lyric;
                            // 同步更新内存对象
                            currentMusic.Lyrics = lyric;
                            currentMusic.TranslatdeLyrics = trans;
                            if (songInMemory != null)
                            {
                                songInMemory.Lyrics = lyric;
                                songInMemory.TranslatdeLyrics = trans;
                            }
                            needUpdateDb = true;
                        }
                    }
                    catch (OperationCanceledException) { Debug.WriteLine("歌词任务取消"); }
                }
            }

            // 3. 统一执行一次数据库 IO
            if (needUpdateDb)
            {
                await MusicDatabaseService.UpdateMusicInfo(currentMusic);
            }

            // 4. 返回解析结果
            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                return SpliteContent(lrcContent,transLrcStr, lyrics);
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

            // 1. 解析原文歌词（包含合并 100ms 内行的逻辑）
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
                    if (lyric != null)
                    {
                        // 赋值翻译文本（如果有多行翻译，也可以考虑用 += "\n" + transText）
                        lyric.TransLateText = transText;
                    }
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

            // 4. 按时间排序返回
            return lyrics.OrderBy(l => l.Time).ToList();
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
            CancelPreviousLyricsTask();
        }
    }
}
