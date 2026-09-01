using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.UI.Text;
using WinUIMusicPlayer.Helper.Animations;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 文本版桌面歌词渲染器：显示当前歌词行（主文本 + 翻译），主色文字。
    /// 可读性由窗口侧环境自适应取色保证（按背景切黑/白文字色），不再需要描边。
    /// 样式为桌面歌词独立设置（经 <see cref="SetStyle"/> 推送），不跟随主界面歌词设置。
    /// </summary>
    public sealed class TextBlockLyricsRenderer : IDesktopLyricsRenderer
    {
        private readonly Grid _root = new();
        private (TextBlock Main, TextBlock Trans)? _mainPair;

        // 换行动画：主文本 + 翻译交给同一实例，保证同一帧换内容
        private readonly TextBlockSwitchAnimator _switchAnimator;

        // 当前已呈现的文本，用于过滤无变化更新（样式刷新等）避免无谓的闪烁
        private string _appliedMain = string.Empty;
        private string _appliedTrans = string.Empty;
        private bool _appliedTransVisible;

        private List<LyricLine>? _lyrics;
        private int _currentIndex = -1;
        private double _offsetMs;
        private long _lastTotalMs;
        private double _fontSize = 36;
        private const double TransOpacity = 0.6;   // 翻译行透明度（与 Canvas 渲染器 SecondaryOpacity 一致）
        private FontFamily? _fontFamily;
        private SolidColorBrush? _mainBrush;
        private int _fontWeight = 400;
        private bool _showTranslation = true;

        public TextBlockLyricsRenderer()
        {
            var (mainPanel, mainPair) = BuildStack();
            _mainPair = mainPair;
            _root.Children.Add(mainPanel);

            _switchAnimator = new TextBlockSwitchAnimator(
                new TextBlock[] { mainPair.Main, mainPair.Trans })
            {
                // 新行自下而上轻微滑入，与淡入合成换行动效
                SlideInDistance = 8,
            };
        }

        public UIElement Content => _root;

        public void SetStyle(DesktopLyricsStyle style)
        {
            _fontSize = Math.Clamp(style.FontSize, 8, 300);
            _fontFamily = string.IsNullOrEmpty(style.FontFamily) ? null : new FontFamily(style.FontFamily);
            _mainBrush = new SolidColorBrush(style.Color);
            _fontWeight = Math.Clamp(style.FontWeight, 100, 900);
            _showTranslation = style.ShowTranslation;
            ApplyFont();
            ApplyColor();
            UpdateText();
        }

        public void SetLyrics(IList<LyricLine>? lyrics)
        {
            _lyrics = lyrics as List<LyricLine> ?? (lyrics is null ? null : [.. lyrics]);
            _currentIndex = -1;
            ApplyCurrentLine(_lastTotalMs - _offsetMs);
        }

        public void SetPlaybackTime(long totalMs)
        {
            _lastTotalMs = totalMs;
            ApplyCurrentLine(totalMs - _offsetMs);
        }

        public void SetOffset(double offsetMs)
        {
            if (Math.Abs(_offsetMs - offsetMs) < 0.5) return;
            _offsetMs = offsetMs;
            ApplyCurrentLine(_lastTotalMs - _offsetMs);
        }

        public void SetIsPlaying(bool isPlaying)
        {
            // 文本渲染无需处理暂停；为未来逐字渲染的时钟暂停预留
        }

        public void Dispose()
        {
        }

        private (StackPanel Panel, (TextBlock Main, TextBlock Trans) Pair) BuildStack()
        {
            var main = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize,
                Foreground = _mainBrush ?? new SolidColorBrush(Colors.White),
            };
            var trans = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize * 0.75,
                Opacity = TransOpacity,
                Foreground = _mainBrush ?? new SolidColorBrush(Colors.White),
                Visibility = Visibility.Collapsed,
            };
            var pair = (main, trans);

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(main);
            panel.Children.Add(trans);
            return (panel, pair);
        }

        private void ApplyCurrentLine(double effectiveMs)
        {
            int newIndex = FindCurrentLineIndex(effectiveMs);
            if (newIndex == _currentIndex) return;
            _currentIndex = newIndex;
            UpdateText();
        }

        private void UpdateText()
        {
            string main = string.Empty;
            string trans = string.Empty;
            if (_lyrics is not null && _currentIndex >= 0 && _currentIndex < _lyrics.Count)
            {
                var line = _lyrics[_currentIndex];
                main = ConcatWords(line.Words);
                trans = line.TransLateText ?? string.Empty;
            }

            bool transVisible = _showTranslation && !string.IsNullOrEmpty(trans);
            if (main == _appliedMain && trans == _appliedTrans && transVisible == _appliedTransVisible)
                return;

            // 上一行有内容才做退场淡出（首行/从空到有直接淡入即可）
            bool fadeOutFirst = _appliedMain.Length > 0;
            _appliedMain = main;
            _appliedTrans = trans;
            _appliedTransVisible = transVisible;

            _switchAnimator.Switch(() =>
            {
                if (_mainPair is not { } pair) return;
                pair.Main.Text = main;
                pair.Trans.Text = trans;
                pair.Trans.Visibility = transVisible
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }, fadeOutFirst);
        }

        private void ApplyFont()
        {
            if (_mainPair is not { } pair) return;
            var fontWeight = new FontWeight { Weight = (ushort)_fontWeight };
            pair.Main.FontSize = _fontSize;
            pair.Trans.FontSize = _fontSize * 0.75;
            pair.Main.FontWeight = fontWeight;
            pair.Trans.FontWeight = fontWeight;
            if (_fontFamily is not null)
            {
                pair.Main.FontFamily = _fontFamily;
                pair.Trans.FontFamily = _fontFamily;
            }
        }

        private void ApplyColor()
        {
            if (_mainPair is { } pair && _mainBrush is not null)
            {
                pair.Main.Foreground = _mainBrush;
                pair.Trans.Foreground = _mainBrush;
            }
        }

        private int FindCurrentLineIndex(double effectiveMs)
        {
            var lyrics = _lyrics;
            if (lyrics is null || lyrics.Count == 0) return -1;

            int lo = 0, hi = lyrics.Count - 1;
            int matched = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (lyrics[mid].StartMs <= effectiveMs)
                {
                    matched = mid;
                    lo = mid + 1;
                }
                else
                {
                    hi = mid - 1;
                }
            }
            return matched;
        }

        private static string ConcatWords(IList<LyricWord> words)
        {
            if (words.Count == 0) return string.Empty;
            if (words.Count == 1) return words[0].Word ?? string.Empty;
            var sb = new StringBuilder(words.Count * 6);
            foreach (var w in words)
            {
                if (!string.IsNullOrEmpty(w.Word))
                    sb.Append(w.Word);
            }
            return sb.ToString();
        }
    }
}
