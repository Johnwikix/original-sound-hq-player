using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public sealed class AnimatedLyricsLineControl : Control
    {
        public event EventHandler<bool>? IsCurrentLineChanged;
        // ── PART ────────────────────────────────────────────────────
        private CanvasControl? _canvas;

        // ── 布局缓存 ─────────────────────────────────────────────────
        private sealed record WordLayout(
            string Text,
            float X,
            float Y,
            float FullWidth,
            float Height
        );

        private readonly List<WordLayout> _wordLayouts = [];
        private string? _cachedText = null;
        private float _cachedWidth = 0f;
        private float _cachedFontSize = 0f;
        private float _canvasHeight = 0f;

        // ── 渲染状态（不用依赖属性，直接字段，避免 boxing） ───────────
        private TimeSpan _currentTime = TimeSpan.Zero;
        private bool _isCurrentLine = false;

        // ── 颜色 ─────────────────────────────────────────────────────
        private static readonly Color DimColor = Color.FromArgb(128, 255, 255, 255);
        private static readonly Color BrightColor = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color TranslateColor = Color.FromArgb(155, 255, 255, 255);
        private const float FeatherWidth = 22f;
        private const float PaddingV = 12f; // 上下各 12px
        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

        public AnimatedLyricsLineControl()
        {
            DefaultStyleKey = typeof(AnimatedLyricsLineControl);
        }

        // ── OnApplyTemplate ──────────────────────────────────────────
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            if (_canvas is not null)
            {
                _canvas.Draw -= OnDraw;
                _canvas.SizeChanged -= OnCanvasSizeChanged;
                _canvas.CreateResources -= OnCreateResources;
            }

            _canvas = GetTemplateChild("PART_Canvas") as CanvasControl;

            if (_canvas is not null)
            {
                _canvas.Draw += OnDraw;
                _canvas.SizeChanged += OnCanvasSizeChanged;
                _canvas.CreateResources += OnCreateResources;
                _canvas.ClearColor = Colors.Transparent;
            }
        }

        // ── 设备重建（设备丢失时重置缓存） ───────────────────────────
        private void OnCreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            InvalidateLayoutCache();
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateLayoutCache();
        }

        // ── 依赖属性 ─────────────────────────────────────────────────

        public static readonly DependencyProperty LyricWordsProperty =
            DependencyProperty.Register(nameof(LyricWords),
                typeof(IList<LyricWord>), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(null, OnVisualPropertyChanged));

        public IList<LyricWord>? LyricWords
        {
            get => (IList<LyricWord>?)GetValue(LyricWordsProperty);
            set => SetValue(LyricWordsProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(nameof(TranslateText),
                typeof(string), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(string.Empty, OnVisualPropertyChanged));

        public string TranslateText
        {
            get => (string)GetValue(TranslateTextProperty);
            set => SetValue(TranslateTextProperty, value);
        }

        public static readonly DependencyProperty IsCurrentLineProperty =
            DependencyProperty.Register(nameof(IsCurrentLine),
                typeof(bool), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(false, OnIsCurrentLineChanged));

        public bool IsCurrentLine
        {
            get => (bool)GetValue(IsCurrentLineProperty);
            set => SetValue(IsCurrentLineProperty, value);
        }

        private static void OnIsCurrentLineChanged(DependencyObject d,
                DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c._isCurrentLine = (bool)e.NewValue;
            if (!c._isCurrentLine)
                c._currentTime = TimeSpan.Zero;
            c.IsCurrentLineChanged?.Invoke(c, c._isCurrentLine); // 加这行
            c._canvas?.Invalidate();
        }

        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(nameof(CurrentPlayingTime),
                typeof(TimeSpan), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            if (!c._isCurrentLine) return;
            c._currentTime = (TimeSpan)e.NewValue;
            c._canvas?.Invalidate();
        }

        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(nameof(LyricsFontSize),
                typeof(double), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(36.0, OnLayoutPropertyChanged));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(nameof(TranslateFontSize),
                typeof(double), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(24.0, OnLayoutPropertyChanged));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        public static readonly DependencyProperty FontFamilyNameProperty =
            DependencyProperty.Register(nameof(FontFamilyName),
                typeof(string), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata("Segoe UI", OnLayoutPropertyChanged));

        public string FontFamilyName
        {
            get => (string)GetValue(FontFamilyNameProperty);
            set => SetValue(FontFamilyNameProperty, value);
        }

        public static readonly DependencyProperty LyricsTextAlignmentProperty =
            DependencyProperty.Register(nameof(LyricsTextAlignment),
                typeof(CanvasHorizontalAlignment), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(CanvasHorizontalAlignment.Left, OnLayoutPropertyChanged));

        public CanvasHorizontalAlignment LyricsTextAlignment
        {
            get => (CanvasHorizontalAlignment)GetValue(LyricsTextAlignmentProperty);
            set => SetValue(LyricsTextAlignmentProperty, value);
        }

        // ── 属性变化回调 ─────────────────────────────────────────────

        // 只需重绘，不需要重新测量（颜色/状态类）
        private static void OnVisualPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
        }

        // 需要重新测量 + 重绘（字号/字体/对齐类）
        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
        }

        // ── 布局缓存 ─────────────────────────────────────────────────
        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            _canvas?.Invalidate();
        }

        /// <summary>
        /// 用 Win2D 测量每个词的精确像素边界。
        /// 三重缓存（text + width + fontSize），只在真正变化时重跑。
        /// </summary>
        private void EnsureLayout(ICanvasResourceCreator creator, float availableWidth)
        {
            var words = LyricWords;
            if (words is null || words.Count == 0) return;

            string fullText = BuildFullText(words);
            float fontSize = (float)LyricsFontSize;
            var alignment = LyricsTextAlignment;

            if (fullText == _cachedText &&
                availableWidth == _cachedWidth &&
                fontSize == _cachedFontSize &&
                alignment == _cachedAlignment)
                return;

            _wordLayouts.Clear();

            // 测量始终用 Left，拿到相对于左边的原始坐标
            using var fmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = fontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left, // 始终 Left
            };

            using var layout = new CanvasTextLayout(
                creator, fullText, fmt, availableWidth, 9999f);

            // 计算整行文字的实际宽度，用于后续对齐偏移
            float lineWidth = (float)layout.LayoutBounds.Width;

            // 根据对齐计算 X 偏移量
            float alignOffsetX = alignment switch
            {
                CanvasHorizontalAlignment.Center => (availableWidth - lineWidth) / 2f,
                CanvasHorizontalAlignment.Right => availableWidth - lineWidth,
                _ => 0f, // Left / Justified
            };

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

                    _wordLayouts.Add(fw > 0 && h > 0
                        ? new WordLayout(
                            w,
                            (float)first.Left + alignOffsetX, // 加上对齐偏移
                            (float)first.Top,
                            fw,
                            h)
                        : new WordLayout(w, 0, 0, 0, 0));
                }
                else
                {
                    _wordLayouts.Add(new WordLayout(w, 0, 0, 0, 0));
                }

                charOffset += w.Length;
            }

            // 计算总高度（不变）
            float lyricsBottom = 0f;
            foreach (var wl in _wordLayouts)
                lyricsBottom = Math.Max(lyricsBottom, wl.Y + wl.Height);

            float translateHeight = 0f;
            if (!string.IsNullOrEmpty(TranslateText))
            {
                using var transFmt = new CanvasTextFormat
                {
                    FontFamily = FontFamilyName,
                    FontSize = (float)TranslateFontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                };
                using var transLayout = new CanvasTextLayout(
                    creator, TranslateText, transFmt, availableWidth, 9999f);
                translateHeight = (float)transLayout.LayoutBounds.Height + 6f;
            }

            _canvasHeight = PaddingV + lyricsBottom + translateHeight + PaddingV;
            if (_canvas is not null) _canvas.Height = _canvasHeight;
            Height = _canvasHeight;

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;
        }

        // ── 核心绘制 ─────────────────────────────────────────────────
        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            var words = LyricWords;
            if (words is null || words.Count == 0) return;

            float w = (float)sender.ActualWidth;
            if (w <= 0) return;

            EnsureLayout(ds, w);
            if (_wordLayouts.Count == 0) return;

            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Auto;

            using var lyricsFmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = (float)LyricsFontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left, // 始终 Left，位置由 wl.X 控制
            };

            int count = Math.Min(words.Count, _wordLayouts.Count);

            // OnDraw 里替换每个词的绘制

            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                var word = words[i];

                if (wl.FullWidth <= 0) continue;

                // 用 Rect 重载替代 (x, y) 重载，避免亚像素抖动
                var wordRect = new Rect(wl.X, wl.Y + PaddingV, wl.FullWidth, wl.Height);

                // 底层暗色，fmt 用 Left 对齐（位置已由 wordRect 控制）
                ds.DrawText(wl.Text, wordRect, DimColor, lyricsFmt);

                if (!_isCurrentLine) continue;

                var elapsed = _currentTime - word.StartTime;
                float progress;
                if (elapsed <= TimeSpan.Zero)
                    progress = 0f;
                else if (word.Duration <= TimeSpan.Zero || elapsed >= word.Duration)
                    progress = 1f;
                else
                    progress = Math.Clamp(
                        (float)(elapsed.TotalMilliseconds / word.Duration.TotalMilliseconds),
                        0f, 1f);

                if (progress <= 0f) continue;

                float revealWidth = wl.FullWidth * progress;

                // 实心高亮区域
                using (ds.CreateLayer(1f,
                    new Rect(wl.X, wl.Y + PaddingV, revealWidth, wl.Height)))
                {
                    ds.DrawText(wl.Text, wordRect, BrightColor, lyricsFmt);
                }

                // 羽化边缘
                if (progress < 1f)
                {
                    float featherX = (float)(wl.X + revealWidth);
                    float featherActual = Math.Min(FeatherWidth, wl.FullWidth - revealWidth);
                    float drawY = (float)(wl.Y + PaddingV);

                    if (featherActual > 0.5f)
                    {
                        // 把羽化区域分成 N 段，每段用递减的 opacity 绘制
                        // 避免任何 Brush 对象创建，纯 float 运算
                        const int steps = 6;
                        float stepW = featherActual / steps;

                        for (int s = 0; s < steps; s++)
                        {
                            float segX = featherX + s * stepW;
                            float opacity = 1f - (float)(s + 1) / steps; // 1.0 → 接近 0
                            var segRect = new Rect(segX, drawY, stepW + 0.5f, wl.Height);

                            using (ds.CreateLayer(opacity, segRect))
                            {
                                ds.DrawText(wl.Text, wordRect, BrightColor, lyricsFmt);
                            }
                        }
                    }
                }
            }

            // ── 翻译文字 ─────────────────────────────────────────────
            if (!string.IsNullOrEmpty(TranslateText))
            {
                float lyricsBottom = 0f;
                foreach (var wl in _wordLayouts)
                    lyricsBottom = Math.Max(lyricsBottom, wl.Y + wl.Height);

                using var transFmt = new CanvasTextFormat
                {
                    FontFamily = FontFamilyName,
                    FontSize = (float)TranslateFontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    // 翻译行用原始对齐，因为它是整行绘制，Rect 约束宽度和测量一致
                    HorizontalAlignment = LyricsTextAlignment,
                };

                ds.DrawText(
                    TranslateText,
                    new Rect(0, PaddingV + lyricsBottom + 6f, w, 9999f),
                    TranslateColor,
                    transFmt);
            }
        }

        // ── 工具 ─────────────────────────────────────────────────────
        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new System.Text.StringBuilder(words.Count * 6);
            foreach (var w in words) sb.Append(w.Word);
            return sb.ToString();
        }
    }
}
