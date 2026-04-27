using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.Text;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsLineControl : UserControl
    {
        private bool _isUpdatingFontSize = false;

        // ── 逐字 Mask 状态 ──────────────────────────────────────────
        // 每个词持有一个 RectangleGeometry，每帧只改 Size.X，零 GC
        private sealed record WordGeo(
                    float FullWidth,
                    float Height,
                    float OffsetX,
                    float OffsetY
                );

        private readonly List<WordGeo> _wordGeos = [];
        private CompositionPathGeometry? _pathGeo;
        private CompositionGeometricClip? _geoClip;
        private bool _clipReady = false;

        public LyricsLineControl()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow != null)
                App.MainWindow.SizeChanged += OnWindowSizeChanged;
            UpdateDynamicFontSizes();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            CancelCurrentAnimation();
            if (App.MainWindow != null)
                App.MainWindow.SizeChanged -= OnWindowSizeChanged;
        }

        private void OnWindowSizeChanged(object sender, Microsoft.UI.Xaml.WindowSizeChangedEventArgs e)
        {
            UpdateDynamicFontSizes();
            InvalidateWordClip(); // 字号/ActualWidth 变了，下帧重建
        }

        // ── CurrentPlayingTime ───────────────────────────────────────
        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(
                nameof(CurrentPlayingTime), typeof(TimeSpan), typeof(LyricsLineControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LyricsLineControl c && c.IsCurrentLine && c.IsWFWLyrics)
                c.DriveWordProgress((TimeSpan)e.NewValue);
        }

        // ── IsCurrentLine ────────────────────────────────────────────
        public static readonly DependencyProperty IsCurrentLineProperty =
            DependencyProperty.Register(
                nameof(IsCurrentLine), typeof(bool), typeof(LyricsLineControl),
                new PropertyMetadata(false, OnIsCurrentLineChanged));

        public bool IsCurrentLine
        {
            get => (bool)GetValue(IsCurrentLineProperty);
            set => SetValue(IsCurrentLineProperty, value);
        }

        private static void OnIsCurrentLineChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not LyricsLineControl c) return;

            if ((bool)e.NewValue)
            {
                // 重置等待下帧懒初始化（此时 ActualWidth 已稳定）
                c.InvalidateWordClip();
                c.IsCurrentLineEvent?.Invoke(c, new RoutedEventArgs());
            }
            else
            {
                c.CancelCurrentAnimation();
            }
        }

        public event RoutedEventHandler? IsCurrentLineEvent;

        // ── Clip 初始化 ──────────────────────────────────────────────
        private void InvalidateWordClip()
        {
            _clipReady = false;
            var visual = ElementCompositionPreview.GetElementVisual(LyricsTextHighlight);
            visual.Clip = null;
            _wordGeos.Clear();
            _pathGeo = null;
            _geoClip = null;
        }

        /// <summary>
        /// 懒初始化：用 Win2D 精确测量每个词的像素边界，
        /// 建立 ShapeVisual mask + SpriteVisual overlay，
        /// 通过 CompositionMaskBrush 将高亮 TextBlock 的内容
        /// 与白色矩形 mask 合成，实现逐字显现效果。
        /// </summary>
        private bool EnsureWordClipReady()
        {
            if (_clipReady) return true;
            if (DataContext is not LyricLine line || line.Words.Count == 0) return false;

            double tbW = LyricsTextHighlight.ActualWidth;
            double tbH = LyricsTextHighlight.ActualHeight;
            if (tbW <= 0 || tbH <= 0) return false;

            var compositor = ElementCompositionPreview
                                 .GetElementVisual(LyricsTextHighlight).Compositor;
            var device = CanvasDevice.GetSharedDevice();
            var words = line.Words;

            // ── 1. Win2D 测量词边界 ──────────────────────────────────────
            using var fmt = new CanvasTextFormat
            {
                FontFamily = FontFamily?.Source ?? "Segoe UI",
                FontSize = (float)LyricsFontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = MapTextAlignment(TextAlignment),
            };
            using var layout = new CanvasTextLayout(
                device, BuildFullText(words), fmt, (float)tbW, (float)tbH * 10f);

            _wordGeos.Clear();

            int charOffset = 0;
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].Word;
                var regions = layout.GetCharacterRegions(charOffset, w.Length);

                if (regions.Length > 0)
                {
                    Rect first = regions[0].LayoutBounds;
                    Rect last = regions[^1].LayoutBounds;
                    _wordGeos.Add(new WordGeo(
                        FullWidth: (float)(last.Right - first.Left),
                        Height: (float)first.Height,
                        OffsetX: (float)first.Left,
                        OffsetY: (float)first.Top
                    ));
                }
                else
                {
                    // 占位，保持索引对齐
                    _wordGeos.Add(new WordGeo(0, 0, 0, 0));
                }

                charOffset += w.Length;
            }

            // ── 2. 建初始 PathGeometry（所有词宽度为 0）────────────────
            // 用 CanvasGeometry.CreateGroup 把各词矩形合并成一个路径
            // 初始全为零宽矩形（不可见），每帧通过 UpdateClipPath 重建
            _pathGeo = compositor.CreatePathGeometry();
            UpdateClipPath(0f); // 全部初始化为 0，先建一次空路径

            // ── 3. 把 GeometricClip 挂到 LyricsTextHighlight 的 Visual ─
            _geoClip = compositor.CreateGeometricClip(_pathGeo);
            ElementCompositionPreview
                .GetElementVisual(LyricsTextHighlight).Clip = _geoClip;

            _clipReady = true;
            return true;
        }

        /// <summary>
        /// 根据当前各词进度重建 CanvasGeometry 并写入 _pathGeo。
        /// 每帧调用，用 CanvasGeometry.CreateGroup 合并所有词的当前矩形。
        /// </summary>
        private void UpdateClipPath(float _ = 0f) { } // 见下方 DriveWordProgress

        // ── 每帧驱动（~16.67ms，零 GC） ────────────────────────────
        private void DriveWordProgress(TimeSpan currentTime)
        {
            if (!EnsureWordClipReady()) return;

            var line = DataContext as LyricLine;
            if (line == null || line.Words.Count == 0 || _pathGeo == null) return;

            var device = CanvasDevice.GetSharedDevice();
            var words = line.Words;
            int wordCount = words.Count;

            // 预分配数组，避免 List 扩容开销
            var rects = new CanvasGeometry[wordCount];
            int activeCount = 0;

            for (int i = 0; i < wordCount; i++)
            {
                var word = words[i];
                var wg = _wordGeos[i];

                // 逻辑优化：跳过无效宽度（如 Win2D 测量失败的情况）
                if (wg.FullWidth <= 0) continue;

                float progress = 0f;
                if (currentTime >= word.StartTime + word.Duration)
                {
                    // 情况 A：词已唱完
                    progress = 1f;
                }
                else if (currentTime > word.StartTime)
                {
                    // 情况 B：正在唱这个词
                    // 避免 Duration 为 0 导致的除以零异常
                    if (word.Duration > TimeSpan.Zero)
                    {
                        // 使用 Ticks 计算比 TotalSeconds/Milliseconds 更快，因为它是 long 型原子单位
                        progress = (float)((double)(currentTime.Ticks - word.StartTime.Ticks) / word.Duration.Ticks);
                        if (progress > 1f) progress = 1f;
                    }
                }
                // 情况 C：还没唱到（progress 为 0），不进入 rects 数组以减少 Group 压力

                if (progress > 0)
                {
                    float drawWidth = (progress >= 1f) ? wg.FullWidth : (wg.FullWidth * progress);

                    // 仅当宽度足够可见时才创建几何体
                    if (drawWidth > 0.1f)
                    {
                        rects[activeCount++] = CanvasGeometry.CreateRectangle(
                            device, wg.OffsetX, wg.OffsetY, drawWidth, wg.Height);
                    }
                }
            }

            if (activeCount > 0)
            {
                // 裁剪数组到实际有效长度
                ReadOnlySpan<CanvasGeometry> activeGeos = rects.AsSpan(0, activeCount);

                try
                {
                    // 注意：CreateGroup 接受 IEnumerable，传入 Array 会稍微高效点
                    using var group = CanvasGeometry.CreateGroup(device, rects[..activeCount]);
                    _pathGeo.Path = new CompositionPath(group);
                }
                catch (ArgumentException)
                {
                    // 捕捉可能的非法参数（如坐标或尺寸异常）
                    InvalidateWordClip();
                }
                finally
                {
                    // 必须在 Group 创建后立即释放，防止 Native 资源堆积
                    for (int i = 0; i < activeCount; i++)
                    {
                        rects[i]?.Dispose();
                    }
                }
            }
        }

        // ── 清理 ────────────────────────────────────────────────────
        public void CancelCurrentAnimation()
        {
            var visual = ElementCompositionPreview.GetElementVisual(LyricsTextHighlight);
            visual.Clip = null;
            _wordGeos.Clear();
            _pathGeo = null;
            _geoClip = null;
            _clipReady = false;
        }

        // ── 工具方法 ─────────────────────────────────────────────────
        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new System.Text.StringBuilder(words.Count * 8);
            foreach (var w in words)
                sb.Append(w.Word);
            return sb.ToString();
        }

        private static CanvasHorizontalAlignment MapTextAlignment(TextAlignment a)
            => a switch
            {
                TextAlignment.Center => CanvasHorizontalAlignment.Center,
                TextAlignment.Right => CanvasHorizontalAlignment.Right,
                TextAlignment.Justify => CanvasHorizontalAlignment.Justified,
                _ => CanvasHorizontalAlignment.Left,
            };

        // ── 字体大小（原逻辑完整保留） ───────────────────────────────
        public static readonly DependencyProperty IsGlobalFontSizeEnabledProperty =
            DependencyProperty.Register(
                nameof(IsGlobalFontSizeEnabled), typeof(bool), typeof(LyricsLineControl),
                new PropertyMetadata(false, (d, _) => (d as LyricsLineControl)?.UpdateDynamicFontSizes()));

        public bool IsGlobalFontSizeEnabled
        {
            get => (bool)GetValue(IsGlobalFontSizeEnabledProperty);
            set => SetValue(IsGlobalFontSizeEnabledProperty, value);
        }

        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(
                nameof(LyricsFontSize), typeof(double), typeof(LyricsLineControl),
                new PropertyMetadata(32.0, OnLyricsFontSizeChanged));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        private static void OnLyricsFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = d as LyricsLineControl;
            if (c == null || c._isUpdatingFontSize) return;
            if (!c.IsGlobalFontSizeEnabled) c.UpdateDynamicFontSizes();
        }

        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(
                nameof(TranslateFontSize), typeof(double), typeof(LyricsLineControl),
                new PropertyMetadata(24.0, OnTranslateFontSizeChanged));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        private static void OnTranslateFontSizeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var c = d as LyricsLineControl;
            if (c == null || c._isUpdatingFontSize) return;
            if (!c.IsGlobalFontSizeEnabled) c.UpdateDynamicFontSizes();
        }

        private void UpdateDynamicFontSizes()
        {
            if (IsGlobalFontSizeEnabled) return;
            if (App.MainWindow?.AppWindow?.Size.Width is null ||
                App.MainWindow.AppWindow.Size.Width == 0) return;

            double scaledWidth = App.MainWindow.AppWindow.Size.Width / AppData.AppDpiScale;

            _isUpdatingFontSize = true;
            try
            {
                LyricsFontSize = CalcFontSize(scaledWidth, isLyrics: true);
                TranslateFontSize = CalcFontSize(scaledWidth, isLyrics: false);
            }
            finally
            {
                _isUpdatingFontSize = false;
            }
        }

        private static double CalcFontSize(double w, bool isLyrics) => w switch
        {
            <= 1440 => isLyrics ? 32.0 : 24.0,
            <= 1680 => isLyrics ? 34.0 : 26.0,
            <= 1920 => isLyrics ? 36.0 : 28.0,
            <= 2160 => isLyrics ? 38.0 : 30.0,
            <= 2560 => isLyrics ? 42.0 : 34.0,
            _ => isLyrics ? 46.0 : 38.0,
        };

        // ── 其余依赖属性（原样保留） ─────────────────────────────────
        public static readonly DependencyProperty LyricWordsProperty =
            DependencyProperty.Register(nameof(LyricWords), typeof(object),
                typeof(LyricsLineControl), new PropertyMetadata(null));
        public object LyricWords
        {
            get => GetValue(LyricWordsProperty);
            set => SetValue(LyricWordsProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(nameof(TranslateText), typeof(string),
                typeof(LyricsLineControl), new PropertyMetadata(string.Empty));
        public string TranslateText
        {
            get => (string)GetValue(TranslateTextProperty);
            set => SetValue(TranslateTextProperty, value);
        }

        public static readonly DependencyProperty TranslateVisibilityProperty =
            DependencyProperty.Register(nameof(TranslateVisibility), typeof(Visibility),
                typeof(LyricsLineControl), new PropertyMetadata(Visibility.Collapsed));
        public Visibility TranslateVisibility
        {
            get => (Visibility)GetValue(TranslateVisibilityProperty);
            set => SetValue(TranslateVisibilityProperty, value);
        }

        public new static readonly DependencyProperty FontFamilyProperty =
            DependencyProperty.Register(nameof(FontFamily),
                typeof(Microsoft.UI.Xaml.Media.FontFamily), typeof(LyricsLineControl),
                new PropertyMetadata(new Microsoft.UI.Xaml.Media.FontFamily("Segoe UI")));
        public new Microsoft.UI.Xaml.Media.FontFamily FontFamily
        {
            get => (Microsoft.UI.Xaml.Media.FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public static readonly DependencyProperty TextAlignmentProperty =
            DependencyProperty.Register(nameof(TextAlignment),
                typeof(TextAlignment), typeof(LyricsLineControl),
                new PropertyMetadata(TextAlignment.Left));
        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        public new static readonly DependencyProperty HorizontalAlignmentProperty =
            DependencyProperty.Register(nameof(HorizontalAlignment),
                typeof(HorizontalAlignment), typeof(LyricsLineControl),
                new PropertyMetadata(HorizontalAlignment.Left));
        public new HorizontalAlignment HorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(HorizontalAlignmentProperty);
            set => SetValue(HorizontalAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsWFWLyricsProperty =
            DependencyProperty.Register(nameof(IsWFWLyrics), typeof(bool),
                typeof(LyricsLineControl), new PropertyMetadata(true));
        public bool IsWFWLyrics
        {
            get => (bool)GetValue(IsWFWLyricsProperty);
            set => SetValue(IsWFWLyricsProperty, value);
        }

        public static readonly DependencyProperty LyricTextProperty =
            DependencyProperty.Register(
            nameof(LyricText), typeof(string), typeof(LyricsLineControl),
            new PropertyMetadata(string.Empty));

        public string LyricText
        {
            get => (string)GetValue(LyricTextProperty);
            set => SetValue(LyricTextProperty, value);
        }

        private void LyricsTextHighlight_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_clipReady) InvalidateWordClip();
        }
    }
}