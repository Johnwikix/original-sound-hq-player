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
            if (d is not LyricsLineControl c) return;
            if (!c.IsCurrentLine || !c.IsWFWLyrics) return;

            // 控件未完成布局时跳过，下一帧 SizeChanged 会触发 InvalidateWordClip 重来
            if (c.ActualWidth <= 0 || c.ActualHeight <= 0) return;

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
            _wordGeos.Clear();
            _pathGeo = null;
            _geoClip = null;

            if (LyricsTextHighlight is null) return;

            LyricsTextHighlight.Opacity = 0.0; // 同上，重建期间保持不可见

            var visual = ElementCompositionPreview.GetElementVisual(LyricsTextHighlight);
            visual.Clip = null;
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

            if (LyricsTextHighlight is null) return false;

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
                FontWeight = new FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = MapTextAlignment(TextAlignment),
            };

            string fullText = BuildFullText(words);
            if (string.IsNullOrEmpty(fullText)) return false;

            using var layout = new CanvasTextLayout(
                device, fullText, fmt, (float)tbW, (float)tbH * 10f);

            _wordGeos.Clear();

            int charOffset = 0;
            for (int i = 0; i < words.Count; i++)
            {
                string w = words[i].Word;
                int len = Math.Max(w.Length, 1);

                var regions = layout.GetCharacterRegions(charOffset, len);

                if (regions.Length > 0)
                {
                    Rect first = regions[0].LayoutBounds;
                    Rect last = regions[^1].LayoutBounds;
                    float fw = (float)(last.Right - first.Left);
                    float h = (float)first.Height;

                    if (fw > 0 && h > 0)
                    {
                        _wordGeos.Add(new WordGeo(
                            FullWidth: fw,
                            Height: h,
                            OffsetX: (float)first.Left,
                            OffsetY: (float)first.Top
                        ));
                    }
                    else
                    {
                        _wordGeos.Add(new WordGeo(0, 0, 0, 0));
                    }
                }
                else
                {
                    _wordGeos.Add(new WordGeo(0, 0, 0, 0));
                }

                charOffset += w.Length;
            }

            // 所有词测量都失败，放弃初始化
            if (_wordGeos.TrueForAll(wg => wg.FullWidth <= 0))
            {
                _wordGeos.Clear();
                return false;
            }

            // ── 2. 建初始 PathGeometry（极小矩形，视觉上不可见） ────────
            _pathGeo = compositor.CreatePathGeometry();

            var initGeo = CanvasGeometry.CreateRectangle(device, 0, 0, 0.001f, 0.001f);
            _pathGeo.Path = new CompositionPath(initGeo);
            initGeo.Dispose();

            // ── 3. 挂 GeometricClip ──────────────────────────────────────
            _geoClip = compositor.CreateGeometricClip(_pathGeo);
            ElementCompositionPreview
                .GetElementVisual(LyricsTextHighlight).Clip = _geoClip;
            LyricsTextHighlight.Opacity = 1.0;
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

            var words = (DataContext as LyricLine)?.Words;

            // _pathGeo 和 words 的 null 保护
            if (words is null || _pathGeo is null || _wordGeos.Count == 0) return;

            var device = CanvasDevice.GetSharedDevice();
            int count = Math.Min(words.Count, _wordGeos.Count);

            // 只收集有效词（FullWidth > 0 且进度 > 0）
            // 全为 0 时传空数组给 CreateGroup 会崩，用 List 动态收集
            var rects = new List<CanvasGeometry>(count);

            try
            {
                for (int i = 0; i < count; i++)
                {
                    var word = words[i];
                    var wg = _wordGeos[i];

                    // 跳过占位词（Win2D 测量失败的词）
                    if (wg.FullWidth <= 0 || wg.Height <= 0) continue;

                    var elapsed = currentTime - word.StartTime;

                    float newWidth;
                    if (elapsed <= TimeSpan.Zero)
                        newWidth = 0f;
                    else if (word.Duration <= TimeSpan.Zero || elapsed >= word.Duration)
                        newWidth = wg.FullWidth;
                    else
                        newWidth = wg.FullWidth *
                                   (float)(elapsed.TotalMilliseconds / word.Duration.TotalMilliseconds);

                    // 跳过宽度为 0 的词，不生成退化矩形
                    if (newWidth < 0.5f) continue;

                    // 用 Math.Min 防止浮点误差超出 FullWidth
                    newWidth = Math.Min(newWidth, wg.FullWidth);

                    rects.Add(CanvasGeometry.CreateRectangle(
                        device,
                        wg.OffsetX, wg.OffsetY,
                        newWidth, wg.Height));
                }

                if (rects.Count == 0)
                {
                    // 没有任何可见词：清空 clip（全遮住）
                    _pathGeo.Path = new CompositionPath(
                        CanvasGeometry.CreateRectangle(device, 0, 0, 0.001f, 0.001f));
                    return;
                }

                // 单个词不需要 CreateGroup，直接用
                if (rects.Count == 1)
                {
                    _pathGeo.Path = new CompositionPath(rects[0]);
                    return;
                }

                using var group = CanvasGeometry.CreateGroup(device, [.. rects]);
                _pathGeo.Path = new CompositionPath(group);
            }
            finally
            {
                // 无论是否异常都释放临时几何体
                foreach (var g in rects) g.Dispose();
            }
        }

        // ── 清理 ────────────────────────────────────────────────────
        public void CancelCurrentAnimation()
        {
            if (LyricsTextHighlight is null) return;
            LyricsTextHighlight.Opacity = 0.0; // 先归零再移除 Clip，避免移除瞬间全亮
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