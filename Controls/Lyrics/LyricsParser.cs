using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public class LyricsParser
    {
        public static async Task SpliteContentAsync(string? lrcContent, string? transLrc, ObservableCollection<LyricLine> lyrics, DispatcherQueue dispatcher)
        {
            if (string.IsNullOrEmpty(lrcContent)) return;

            // --- 1. 后台线程执行耗时解析 (Task.Run) ---
            var processedLyrics = await Task.Run(() =>
            {
                var tempList = new List<LyricLine>();

                // 解析原文
                ParseLrcToLines(lrcContent, (time, text) =>
                {
                    var line = new LyricLine { Time = time, IsCurrent = false };
                    foreach (var w in SplitEverything(text))
                    {
                        line.Words.Add(new LyricWord { Word = w });
                    }
                    tempList.Add(line);
                });

                // 排序（在内存 List 中排序比在 ObservableCollection 频繁 Insert 快得多）
                tempList = tempList.OrderBy(l => l.Time).ToList();

                // 解析并合并翻译
                if (!string.IsNullOrEmpty(transLrc))
                {
                    ParseLrcToLines(transLrc, (time, transText) =>
                    {
                        var lyric = tempList.FirstOrDefault(l => Math.Abs((l.Time - time).TotalMilliseconds) <= 50);
                        if (lyric != null) lyric.TransLateText = transText;
                    });
                }

                // 计算时长逻辑（依然在后台计算）
                for (int i = 0; i < tempList.Count; i++)
                {
                    var currentLine = tempList[i];
                    TimeSpan lineDuration = (i < tempList.Count - 1)
                        ? tempList[i + 1].Time - currentLine.Time
                        : TimeSpan.FromSeconds(5);

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

                return tempList;
            });

            // --- 2. 只有 UI 集合操作切回 UI 线程 ---
            dispatcher.TryEnqueue(() =>
            {
                // 如果在解析期间用户已经切换了歌曲，可以在这里做一次防护判断
                lyrics.Clear();
                foreach (var line in processedLyrics)
                {
                    lyrics.Add(line);
                }
            });
        }
        /// <summary>
        /// 核心解析逻辑：处理时间标签并提取文本
        /// </summary>
        private static void ParseLrcToLines(string content, Action<TimeSpan, string> onLineParsed)
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
    }
}
