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
    /// 双层嵌套叠加——强度与 Win2D 渲染器同一条曲线（DesktopLyricsShadow.SplitStrength：
    /// 内层 min(1,s) + 外层 clamp(s-1,0,1)），颜色为文字色反相；混合背景上黑白二选一
    /// 必有一侧对比度不足，阴影是可读性保底，均匀背景下近乎无感；环境自适应取色仍按
    /// 背景切黑/白文字色（见 DesktopLyricsAdaptiveColor）。
    /// 样式为桌面歌词独立设置（经 <see cref="SetStyle"/> 推送），不跟随主界面歌词设置。
    /// </summary>
    public sealed class TextBlockLyricsRenderer : IDesktopLyricsRenderer
    {
        private readonly Grid _root = new();
        private (TextBlock Main, TextBlock Trans)? _mainPair;
        // 双层阴影包裹（全限定引用：DevWinUI 命名空间下有同名 LyricLine，不能 using 进来）：
        // 内层贴文字，外层套在内层外（Content = 内层），两者都用文字字形 AlphaMask；
        // 强度 ≤100%（s ≤ 1）时外层透明度为 0 不参与
        private DevWinUI.CompositionShadow? _mainShadowInner;
        private DevWinUI.CompositionShadow? _mainShadowOuter;
        private DevWinUI.CompositionShadow? _transShadowInner;
        private DevWinUI.CompositionShadow? _transShadowOuter;

        // 换行动画：最外层阴影包裹（而非 TextBlock 本身）交给同一实例，
        // 文字与两层阴影在同一视觉子树里同步淡入淡出/滑入
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
        // 阴影强度（0–2，= 滑块百分比 / 50）：内层透明度 = min(1,s)，外层 = clamp(s-1,0,1)；
        // 0 = 关闭（跳过 mask 维护，两层不透明度归零不画）
        private double _shadowStrength = 1.0;

        public TextBlockLyricsRenderer()
        {
            var (mainPanel, mainPair) = BuildStack();
            _mainPair = mainPair;
            _root.Children.Add(mainPanel);

            _switchAnimator = new TextBlockSwitchAnimator(
                new FrameworkElement[] { _mainShadowOuter!, _transShadowOuter! })
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
            _shadowStrength = Math.Clamp(style.ShadowAmount, 0, 100) / 50.0;
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

            // 双层阴影包裹（内层套文字，外层套内层）：DevWinUI 默认模板为
            // Grid{阴影Border + Content}，影子 SpriteVisual 挂在文字后方的 Border 上，
            // 字形 mask 见 RefreshShadowMask；强度 >50% 时外层以剩余强度叠加
            _mainShadowInner = CreateTextShadow(main);
            _mainShadowOuter = CreateTextShadow(_mainShadowInner);
            _transShadowInner = CreateTextShadow(trans);
            _transShadowOuter = CreateTextShadow(_transShadowInner);

            // 包裹层自身也要贴住内容宽度（内外两层都要）：SpriteVisual 固定在 Control 原点，
            // 而包裹层在 StackPanel 里默认被拉伸到面板宽度（= 较宽的主行宽度），居中的窄
            // 翻译行内容相对原点偏右，阴影整体左偏 (Control宽-内容宽)/2——翻译行阴影左偏
            // 的根因。DevWinUI 只同步阴影尺寸不同步偏移，按"内容撑满 Control"设计，必须贴住。
            _mainShadowInner.HorizontalAlignment = HorizontalAlignment.Center;
            _mainShadowOuter.HorizontalAlignment = HorizontalAlignment.Center;
            _transShadowInner.HorizontalAlignment = HorizontalAlignment.Center;
            _transShadowOuter.HorizontalAlignment = HorizontalAlignment.Center;

            // 布局尺寸变化（换字/字体/换行）后重取 mask，阴影形状不滞后
            main.SizeChanged += (_, _) => RefreshShadowMask();
            trans.SizeChanged += (_, _) => RefreshShadowMask();

            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            panel.Children.Add(_mainShadowOuter);
            panel.Children.Add(_transShadowOuter);
            return (panel, pair);
        }

        private static DevWinUI.CompositionShadow CreateTextShadow(FrameworkElement content)
        {
            // 零偏移光晕式阴影：保可读性且不喧宾夺主；颜色/强度随用户设置（见 ApplyColor）
            return new DevWinUI.CompositionShadow
            {
                Content = content,
                BlurRadius = DesktopLyricsShadow.BlurRadius,
                OffsetX = 0,
                OffsetY = 0,
                Color = Colors.Black,
            };
        }

        /// <summary>重取四个字形 AlphaMask（主/翻译 × 内/外层）。文档不保证 GetAlphaMask
        /// 返回的 brush 随文本/字体变化自动更新，换字与样式变化后需显式刷新。
        /// 外层的 Content 不是 TextBlock，DevWinUI 的自动 mask 对它退化为矩形阴影，
        /// 必须手动喂与内层相同的字形 mask。先同步兜底一次，再推到低优先级队列——
        /// 让新文本先完成一次布局后取正确几何，避免阴影形状滞后一拍。
        /// 阴影关闭（强度 0）时跳过 mask 维护。</summary>
        private void RefreshShadowMask()
        {
            if (_mainShadowInner is null || _mainShadowOuter is null ||
                _transShadowInner is null || _transShadowOuter is null ||
                _shadowStrength <= 0) return;
            ApplyMasks();
            _root.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyMasks);
        }

        private void ApplyMasks()
        {
            if (_mainPair is not { } pair ||
                _mainShadowInner is null || _mainShadowOuter is null ||
                _transShadowInner is null || _transShadowOuter is null) return;
            var mainMask = pair.Main.GetAlphaMask();
            _mainShadowInner.Mask = mainMask;
            _mainShadowOuter.Mask = mainMask;
            var transMask = pair.Trans.GetAlphaMask();
            _transShadowInner.Mask = transMask;
            _transShadowOuter.Mask = transMask;
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

            // 阴影色跟随文字色反相；两层透明度公式与 Win2D 渲染器同源
            // （DesktopLyricsShadow.SplitStrength）：内层 = min(1,s)，外层 = clamp(s-1,0,1)
            var color = DesktopLyricsShadow.Invert(_mainBrush.Color);
            float innerOpacity = (float)Math.Min(1.0, _shadowStrength);
            float outerOpacity = (float)Math.Clamp(_shadowStrength - 1.0, 0.0, 1.0);
            ApplyShadowLayer(_mainShadowInner, _mainShadowOuter, color, innerOpacity, outerOpacity);
            ApplyShadowLayer(_transShadowInner, _transShadowOuter, color, innerOpacity, outerOpacity);
        }

        private static void ApplyShadowLayer(
            DevWinUI.CompositionShadow? inner,
            DevWinUI.CompositionShadow? outer,
            Windows.UI.Color color,
            float innerOpacity,
            float outerOpacity)
        {
            if (inner is null || outer is null) return;
            inner.Color = color;
            inner.ShadowOpacity = innerOpacity;
            outer.Color = color;
            outer.ShadowOpacity = outerOpacity;
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
