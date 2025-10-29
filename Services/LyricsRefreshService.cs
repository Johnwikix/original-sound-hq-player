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
        public List<LyricLine> _lyrics = [];
        private CancellationTokenSource _lyricsCancellationTokenSource;
        private MusicBrowseViewModel MusicBrowseViewModel { get; }
        public LyricsRefreshService()
        {
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
        }
        public void UpdateLyrics(TimeSpan currentPosition)
        {
            if (_lyrics.Count == 0)
                return;
            // 查找当前应显示的歌词
            int currentIndex = -1;
            for (int i = 0; i < _lyrics.Count; i++)
            {
                // 找到时间戳小于等于当前播放位置的最后一条歌词
                if (_lyrics[i].Time <= currentPosition)
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
            if (_lyrics.Count == 0)
                return;
            MusicBrowseViewModel.UpdateLyricsToUI(0);
        }

        public async Task SetLyrics()
        {
            CancelPreviousLyricsTask();
            _lyrics.Clear();
            string? lrcContent = GetLyricsContentFromLrc(AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Path);
            var lyricsContent = await ParseLrcLyrics(lrcContent);
            if (lyricsContent is not null)
            {
                _lyrics = lyricsContent;
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
            _lyricsCancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = _lyricsCancellationTokenSource.Token;
            List<LyricLine> lyrics = new List<LyricLine>();
            if (!string.IsNullOrWhiteSpace(lrcContent))
            {
                return SpliteContent(lrcContent, lyrics);
            }
            lrcContent = AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id)?.Lyrics;
            if (string.IsNullOrWhiteSpace(lrcContent))
            {
                if (AppSettings.isAutoLyricsEnabled)
                {
                    try
                    {
                        var autoLyrics = string.Empty;
                        autoLyrics = await LrcService.GetLyricsAsync(
                            MusicBrowseViewModel.CurrentPlayingMusic.Title,
                            MusicBrowseViewModel.CurrentPlayingMusic.Album,
                            MusicBrowseViewModel.CurrentPlayingMusic.Author,
                            cancellationToken);

                        if (autoLyrics is not null)
                        {
                            lrcContent = autoLyrics;
                            MusicBrowseViewModel.CurrentPlayingMusic.Lyrics = lrcContent;
                            cancellationToken.ThrowIfCancellationRequested();
                            await MusicDatabaseService.UpdateMusicInfo(MusicBrowseViewModel.CurrentPlayingMusic);
                            AppData.allSongs.AsValueEnumerable().FirstOrDefault(m => m.Id == MusicBrowseViewModel.CurrentPlayingMusic?.Id).Lyrics = lrcContent;
                            return SpliteContent(lrcContent, lyrics);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Debug.WriteLine("歌词获取任务已被取消");
                    }
                    return null;
                }
                else
                {
                    lyrics.Add(new LyricLine
                    {
                        Text = ToolUtils.GetString("LyricsGetFailed"),
                        Time = TimeSpan.Zero,
                        IsCurrent = true
                    });
                    return lyrics;
                }
            }
            else
            {
                return SpliteContent(lrcContent, lyrics);
            }
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

        private List<LyricLine> SpliteContent(string lrcContent, List<LyricLine> lyrics)
        {
            string[] lines = lrcContent.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string line in lines)
            {
                string trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || !trimmedLine.StartsWith("["))
                    continue;

                // 匹配时间标签 [mm:ss.xx]
                Match timeMatch = Regex.Match(trimmedLine, @"\[(\d{2}):(\d{2})\.(\d{2,3})\]");
                if (timeMatch.Success)
                {
                    int minutes = int.Parse(timeMatch.Groups[1].Value);
                    int seconds = int.Parse(timeMatch.Groups[2].Value);
                    string millisecondStr = timeMatch.Groups[3].Value;
                    int milliseconds;

                    // 根据毫秒部分的长度处理不同格式
                    if (millisecondStr.Length == 2)
                    {
                        milliseconds = int.Parse(millisecondStr) * 10;
                    }
                    else
                    {
                        milliseconds = int.Parse(millisecondStr);
                    }

                    TimeSpan time = new TimeSpan(0, 0, minutes, seconds, milliseconds);

                    // 提取歌词文本（时间标签后的所有内容）
                    string text = trimmedLine.Substring(timeMatch.Length).Trim();

                    // 如果是空行或元信息行（如作词作曲），跳过或添加为特殊行
                    if (string.IsNullOrEmpty(text))
                        continue;

                    bool found = false;
                    foreach (var lyric in lyrics)
                    {
                        if (Math.Abs((lyric.Time - time).TotalMilliseconds) <= 100)
                        {
                            lyric.Text += "\n" + text;
                            found = true;
                            break;
                        }
                    }
                    if (!found)
                    {
                        lyrics.Add(new LyricLine
                        {
                            Time = time,
                            Text = text,
                            IsCurrent = false
                        });
                    }
                }
            }
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
            return lyrics.AsValueEnumerable().OrderBy(l => l.Time).ToList();
        }

        public void Dispose()
        {
            CancelPreviousLyricsTask();
        }
    }
}
