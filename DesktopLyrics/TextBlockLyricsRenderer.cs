using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Text;
using Windows.Foundation;
using Windows.UI.Text;
using WinUIMusicPlayer.Helper.Animations;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 文本版桌面歌词渲染器：显示当前歌词行（主文本 + 翻译），主色文字。
    /// 文字带 Composition 软阴影（DevWinUI CompositionShadow，DropShadow + 字形 AlphaMask），
    /// 颜色为文字色反相（见 DesktopLyricsShadow）：混合背景上黑白二选一必有一侧对比度不足，
    /// 阴影是可读性保底，均匀背景下近乎无感；环境自适应取色仍按背景切黑/白文字色
    /// （见 DesktopLyricsAdaptiveColor）。
    /// 样式为桌面歌词独立设置（经 <see cref="SetStyle"/> 推送），不跟随主界面歌词设置。
    /// </summary>
    public sealed class TextBlockLyricsRenderer : IDesktopLyricsRenderer
    {
        private readonly Grid _root = new();
        private (TextBlock Main, TextBlock Trans)? _mainPair;
        // 全限定引用：DevWinUI 命名空间下有同名 LyricLine，不能 using 进来
        private DevWinUI.CompositionShadow? _mainShadow;
        private DevWinUI.CompositionShadow? _transShadow;

        // 换行动画：阴影包裹层（而非 TextBlock 本身）交给同一实例，
        // 文字与阴影在同一视觉子树里同步淡入淡出/滑入
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
        private double _shadowOpacity = 0.75;   // 0 = 关闭（跳过 mask 维护，Composition 侧不透明度归零不画）

        public TextBlockLyricsRenderer()
        {
            var (mainPanel, mainPair) = BuildStack();
            _mainPair = mainPair;
            _root.Children.Add(mainPanel);

            _switchAnimator = new TextBlockSwitchAnimator(
                new FrameworkElement[] { _mainShadow!, _transShadow! })
            {
                // 新行自下而上轻微滑入，与淡入合成换行动效
                SlideInDistance = 8,
            };
        }

        public UIElement Content => _root;

        /// <summary>最近一次实际绘制的文本区域（元素坐标 DIP）：主文本边界，翻译行可见则合并。
        /// 纯 UI 线程按需计算（窗口取色轮询时调用），无需布局事件钩子；
        /// 无文本或尚未完成布局时返回 null（窗口回退窗口环带采样）。</summary>
        public Rect? LastTextBounds
        {
            get
            {
                if (_mainPair is not { } pair || _root.XamlRoot is null) return null;
                if (pair.Main.ActualWidth <= 1 || pair.Main.ActualHeight <= 1) return null;
                var bounds = pair.Main.TransformToVisual(null)
                    .TransformBounds(new Rect(0, 0, pair.Main.ActualWidth, pair.Main.ActualHeight));
                if (pair.Trans.Visibility == Visibility.Visible && pair.Trans.ActualWidth > 1)
                {
                    var translation = pair.Trans.TransformToVisual(null)
                        .TransformBounds(new Rect(0, 0, pair.Trans.ActualWidth, pair.Trans.ActualHeight));
                    bounds.Union(translation);
                }
                return bounds;
            }
        }

        public void SetStyle(DesktopLyricsStyle style)
        {
            _fontSize = Math.Clamp(style.FontSize, 8, 300);
            _fontFamily = string.IsNullOrEmpty(style.FontFamily) ? null : new FontFamily(style.FontFamily);
            _mainBrush = new SolidColorBrush(style.Color);
            _fontWeight = Math.Clamp(style.FontWeight, 100, 900);
            _showTranslation = style.ShowTranslation;
            _shadowOpacity = Math.Clamp(style.ShadowAmount, 0, 100) / 100.0;
            ApplyFont();
            ApplyColor();
            UpdateText();
            RefreshShadowMask();   // 字体/字号变化而文本未变时 UpdateText 会早退，mask 需单独刷新
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
            // HorizontalAlignment.Center 让元素宽度贴住文字本身：字形 AlphaMask 与
            // 阴影 SpriteVisual（按 Content 实际尺寸布置）几何完全重合，杜绝
            // mask 拉伸/对齐歧义造成的阴影错位（此前翻译行阴影左偏即源于此）
            var main = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize,
                Foreground = _mainBrush ?? new SolidColorBrush(Colors.White),
            };
            var trans = new TextBlock
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                FontSize = _fontSize * 0.75,
                Opacity = TransOpacity,
                Foreground = _mainBrush ?? new SolidColorBrush(Colors.White),
                Visibility = Visibility.Collapsed,
            };
            var pair = (main, trans);

            // 阴影包裹层：DevWinUI 默认模板为 Grid{阴影Border + Content}，
            // 影子 SpriteVisual 挂在文字后方的 Border 上，字形 mask 见 RefreshShadowMask
            _mainShadow = CreateTextShadow(main);
            _transShadow = CreateTextShadow(trans);
            // 包裹层自身也要贴住内容宽度：SpriteVisual 固定在 Control 原点，而包裹层在
            // StackPanel 里默认被拉伸到面板宽度（= 较宽的主行宽度），居中的窄翻译行内容
            // 相对原点偏右，阴影整体左偏 (Control宽-内容宽)/2——翻译行阴影左偏的根因。
            // DevWinUI 只同步阴影尺寸不同步偏移，按"内容撑满 Control"设计，必须贴住。
            _mainShadow.HorizontalAlignment = HorizontalAlignment.Center;
            _transShadow.HorizontalAlignment = HorizontalAlignment.Center;

            // 布局尺寸变化（换字/字体/换行）后重取 mask，阴影形状不滞后
            main.SizeChanged += (_, _) => RefreshShadowMask();
            trans.SizeChanged += (_, _) => RefreshShadowMask();

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(_mainShadow);
            panel.Children.Add(_transShadow);
            return (panel, pair);
        }

        private DevWinUI.CompositionShadow CreateTextShadow(TextBlock text)
        {
            // 零偏移光晕式阴影：保可读性且不喧宾夺主；颜色/强度随用户设置（见 ApplyColor）
            return new DevWinUI.CompositionShadow
            {
                Content = text,
                BlurRadius = DesktopLyricsShadow.BlurRadius,
                ShadowOpacity = _shadowOpacity,
                OffsetX = 0,
                OffsetY = 0,
                Color = Colors.Black,
            };
        }

        /// <summary>重取两个字形 AlphaMask。文档不保证 GetAlphaMask 返回的 brush 随
        /// 文本/字体变化自动更新，换字与样式变化后需显式刷新；推到低优先级队列，
        /// 让新文本先完成一次布局再取几何，避免阴影形状滞后一拍。
        /// 阴影关闭（强度 0）时跳过 mask 维护。</summary>
        private void RefreshShadowMask()
        {
            if (_mainShadow is null || _transShadow is null || _shadowOpacity <= 0) return;
            _root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                if (_mainPair is not { } pair) return;
                _mainShadow.Mask = pair.Main.GetAlphaMask();
                _transShadow.Mask = pair.Trans.GetAlphaMask();
            });
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
                RefreshShadowMask();
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
            if (_mainPair is not { } pair || _mainBrush is null) return;
            pair.Main.Foreground = _mainBrush;
            pair.Trans.Foreground = _mainBrush;
            // 阴影色跟随文字色反相，强度取用户设置（0 = 关闭）：自适应黑/白与自定义颜色都成立
            if (_mainShadow is { } mainShadow)
            {
                mainShadow.Color = DesktopLyricsShadow.Invert(_mainBrush.Color);
                mainShadow.ShadowOpacity = _shadowOpacity;
            }
            if (_transShadow is { } transShadow)
            {
                transShadow.Color = DesktopLyricsShadow.Invert(_mainBrush.Color);
                transShadow.ShadowOpacity = _shadowOpacity;
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
