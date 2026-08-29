using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Messages;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Text;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 文本版桌面歌词渲染器：显示当前歌词行（主文本 + 翻译），
    /// 白字 + 4 向偏移黑字模拟描边。样式（字号/字体/颜色/翻译透明度）
    /// 跟随主界面歌词设置，经 LyricsFontSizeBus / LyricsSettingsBus 同步。
    /// </summary>
    public sealed class TextBlockLyricsRenderer : IDesktopLyricsRenderer
    {
        private static readonly (double X, double Y)[] OutlineOffsets = [(-1.5, 0), (1.5, 0), (0, -1.5), (0, 1.5)];

        private readonly Grid _root = new();
        private readonly List<(TextBlock Main, TextBlock Trans)> _shadowPairs = [];
        private (TextBlock Main, TextBlock Trans)? _mainPair;

        private List<LyricLine>? _lyrics;
        private int _currentIndex = -1;
        private double _offsetMs;
        private long _lastTotalMs;
        private double _fontSize = 32;
        private double _transOpacity = 0.6;
        private FontFamily? _fontFamily;
        private SolidColorBrush? _mainBrush;

        public TextBlockLyricsRenderer()
        {
            foreach (var (x, y) in OutlineOffsets)
                _root.Children.Add(BuildStack(isShadow: true, x, y));
            _root.Children.Add(BuildStack(isShadow: false, 0, 0));

            LyricsFontSizeBus.Changed += OnFontSizeChanged;
            LyricsSettingsBus.SyncRequested += OnLyricsSettingsSync;
        }

        public UIElement Content => _root;

        private IEnumerable<(TextBlock Main, TextBlock Trans)> ShadowAndMainPairs
        {
            get
            {
                foreach (var pair in _shadowPairs)
                    yield return pair;
                if (_mainPair is { } main)
                    yield return main;
            }
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
            LyricsFontSizeBus.Changed -= OnFontSizeChanged;
            LyricsSettingsBus.SyncRequested -= OnLyricsSettingsSync;
        }

        private StackPanel BuildStack(bool isShadow, double offsetX, double offsetY)
        {
            var main = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize,
                Foreground = isShadow ? new SolidColorBrush(Colors.Black) : (_mainBrush ?? new SolidColorBrush(Colors.White)),
            };
            var trans = new TextBlock
            {
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize * 0.75,
                Opacity = _transOpacity,
                Foreground = isShadow ? new SolidColorBrush(Colors.Black) : (_mainBrush ?? new SolidColorBrush(Colors.White)),
                Visibility = Visibility.Collapsed,
            };
            var pair = (main, trans);
            if (isShadow) _shadowPairs.Add(pair);
            else _mainPair = pair;

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(offsetX, offsetY, 0, 0),
            };
            panel.Children.Add(main);
            panel.Children.Add(trans);
            return panel;
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

            foreach (var (mainTb, transTb) in ShadowAndMainPairs)
            {
                mainTb.Text = main;
                transTb.Text = trans;
                transTb.Visibility = string.IsNullOrEmpty(trans) ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void ApplyFont()
        {
            foreach (var (mainTb, transTb) in ShadowAndMainPairs)
            {
                mainTb.FontSize = _fontSize;
                transTb.FontSize = _fontSize * 0.75;
                if (_fontFamily is not null)
                {
                    mainTb.FontFamily = _fontFamily;
                    transTb.FontFamily = _fontFamily;
                }
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

        private void OnFontSizeChanged(double value)
        {
            double newSize = value * 0.8;
            if (Math.Abs(_fontSize - newSize) < 0.5) return;
            _fontSize = newSize;
            ApplyFont();
        }

        private void OnLyricsSettingsSync(LyricsSettingsBus.Settings s)
        {
            _fontFamily = string.IsNullOrEmpty(s.FontFamilyName) ? null : new FontFamily(s.FontFamilyName);
            _mainBrush = new SolidColorBrush(s.IsCustomColorEnabled ? s.LyricsCustomColor : Colors.White);
            _transOpacity = Math.Clamp(s.TranslatedOpacity, 0.1, 1.0);
            ApplyFont();
            ApplyColor();
            UpdateText();
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
