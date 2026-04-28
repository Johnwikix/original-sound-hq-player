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
        private float _measuredHeight = 0f;

        // ── 渲染状态 ─────────────────────────────────────────────────
        private TimeSpan _currentTime = TimeSpan.Zero;
        private bool _isCurrentLine = false;

        // ── 颜色 ─────────────────────────────────────────────────────
        private Color _dimColor = Color.FromArgb(128, 0, 0, 0);
        private Color _brightColor = Color.FromArgb(255, 255, 255, 255);
        private Color _translateColor = Color.FromArgb(155, 255, 255, 255);

        private const float FeatherWidth = 22f;
        private const float PaddingV = 12f;

        /// <summary>
        /// 额外内边距，防止整体 Blur 被 CanvasControl 边缘硬裁切。
        /// </summary>
        private const float RenderPadding = 10f;

        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

        public AnimatedLyricsLineControl()
        {
            DefaultStyleKey = typeof(AnimatedLyricsLineControl);
            UpdateColors(true);
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

        // ── MeasureOverride ──────────────────────────────────────────
        protected override Size MeasureOverride(Size availableSize)
        {
            float w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 400f
                : (float)availableSize.Width;

            if (_measuredHeight > 0f && Math.Abs(_cachedWidth - w) < 1f)
                return new Size(availableSize.Width, _measuredHeight);

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                EnsureLayout(device, w);
                if (_measuredHeight > 0f)
                    return new Size(availableSize.Width, _measuredHeight);
            }
            catch { }

            return base.MeasureOverride(availableSize);
        }

        // ── 设备重建 ─────────────────────────────────────────────────
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
                new PropertyMetadata(null, OnLayoutPropertyChanged));

        public IList<LyricWord>? LyricWords
        {
            get => (IList<LyricWord>?)GetValue(LyricWordsProperty);
            set => SetValue(LyricWordsProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(nameof(TranslateText),
                typeof(string), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(string.Empty, OnLayoutPropertyChanged));

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
            c.IsCurrentLineChanged?.Invoke(c, c._isCurrentLine);
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

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark),
                typeof(bool), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(false, OnIsDarkChanged));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        private static void OnIsDarkChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.UpdateColors((bool)e.NewValue);
            c._canvas?.Invalidate();
        }

        private void UpdateColors(bool isDark)
        {
            if (isDark)
            {
                _dimColor = Color.FromArgb(128, 255, 255, 255);
                _brightColor = Color.FromArgb(255, 255, 255, 255);
                _translateColor = Color.FromArgb(155, 255, 255, 255);
            }
            else
            {
                _dimColor = Color.FromArgb(100, 0, 0, 0);
                _brightColor = Color.FromArgb(255, 0, 0, 0);
                _translateColor = Color.FromArgb(155, 0, 0, 0);
            }
        }

        // ── 属性变化回调 ─────────────────────────────────────────────

        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
            c.InvalidateMeasure();
        }

        // ── 布局缓存 ─────────────────────────────────────────────────
        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            _measuredHeight = 0f;
            _canvas?.Invalidate();
        }

        private void EnsureLayout(ICanvasResourceCreator creator, float availableWidth)
        {
            var words = LyricWords;
            if (words is null || words.Count == 0) return;

            string fullText = BuildFullText(words);
            float fontSize = (float)LyricsFontSize;
            var alignment = LyricsTextAlignment;

            if (fullText == _cachedText &&
                Math.Abs(availableWidth - _cachedWidth) < 1f &&
                fontSize == _cachedFontSize &&
                alignment == _cachedAlignment)
                return;

            _wordLayouts.Clear();

            // 布局宽度去掉左右 RenderPadding，让文字在 padding 内侧排列
            float layoutWidth = Math.Max(1f, availableWidth - RenderPadding * 2f);

            using var fmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = fontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            using var layout = new CanvasTextLayout(creator, fullText, fmt, layoutWidth, 9999f);

            float lineWidth = (float)layout.LayoutBounds.Width;
            float alignOffsetX = alignment switch
            {
                CanvasHorizontalAlignment.Center => (layoutWidth - lineWidth) / 2f,
                CanvasHorizontalAlignment.Right => layoutWidth - lineWidth,
                _ => 0f,
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

                    // X 坐标加上 RenderPadding 偏移
                    _wordLayouts.Add(fw > 0 && h > 0
                        ? new WordLayout(w,
                            (float)first.Left + alignOffsetX + RenderPadding,
                            (float)first.Top,
                            fw, h)
                        : new WordLayout(w, RenderPadding, 0, 0, 0));
                }
                else
                {
                    _wordLayouts.Add(new WordLayout(w, RenderPadding, 0, 0, 0));
                }

                charOffset += w.Length;
            }

            float lyricsHeight = (float)layout.LayoutBounds.Height;

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
                    creator, TranslateText, transFmt, layoutWidth, 9999f);
                translateHeight = (float)transLayout.DrawBounds.Height + 6f;
            }

            // 总高度也加上上下 RenderPadding
            float totalHeight = RenderPadding + PaddingV + lyricsHeight + translateHeight + PaddingV + RenderPadding;

            _canvasHeight = totalHeight;
            _measuredHeight = totalHeight;

            if (_canvas is not null) _canvas.Height = totalHeight;
            Height = totalHeight;

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

            // ── 把所有内容先画到 CommandList，再整体做 Blur ──────────────
            using var cl = new CanvasCommandList(ds);
            using (var clDs = cl.CreateDrawingSession())
            {
                DrawContent(clDs, words, w);
            }

            // 非当前行加最大 2 的模糊；当前行不模糊（或可设为极小值 0）
            float blurAmount = _isCurrentLine ? 0f : 1.5f;

            if (blurAmount > 0f)
            {
                using var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
                {
                    Source = cl,
                    BlurAmount = blurAmount,
                    BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Soft,
                };
                ds.DrawImage(blur);
            }
            else
            {
                ds.DrawImage(cl);
            }
        }

        /// <summary>
        /// 将歌词与翻译绘制到指定 DrawingSession（可以是屏幕或 CommandList）。
        /// 所有坐标已含 RenderPadding 偏移。
        /// </summary>
        private void DrawContent(CanvasDrawingSession ds,
            IList<LyricWord> words, float totalWidth)
        {
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Auto;

            float layoutWidth = Math.Max(1f, totalWidth - RenderPadding * 2f);
            float drawOffsetY = RenderPadding; // 上方 RenderPadding

            using var lyricsFmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = (float)LyricsFontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            int count = Math.Min(words.Count, _wordLayouts.Count);

            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                var word = words[i];
                if (wl.FullWidth <= 0) continue;

                // wordRect 的 Y 加上 drawOffsetY（RenderPadding）和 PaddingV
                var wordRect = new Rect(
                    wl.X,
                    wl.Y + drawOffsetY + PaddingV,
                    wl.FullWidth,
                    wl.Height);

                // 底层暗色
                ds.DrawText(wl.Text, wordRect, _dimColor, lyricsFmt);

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
                float drawY = (float)(wl.Y + drawOffsetY + PaddingV);
                float wordH = wl.Height;

                // ── 实心亮色高亮区域 ──────────────────────────────────────
                using (ds.CreateLayer(1f, new Rect(wl.X, drawY, revealWidth, wordH)))
                {
                    ds.DrawText(wl.Text, wordRect, _brightColor, lyricsFmt);
                }

                // ── 羽化边缘 ─────────────────────────────────────────────
                if (progress < 1f)
                {
                    float featherActual = Math.Min(FeatherWidth, wl.FullWidth - revealWidth);
                    if (featherActual > 0.5f)
                    {
                        float featherX = (float)(wl.X + revealWidth);

                        var gradStops = new CanvasGradientStop[]
                        {
                            new() { Color = _brightColor, Position = 0f },
                            new() { Color = Color.FromArgb(0,
                                        _brightColor.R,
                                        _brightColor.G,
                                        _brightColor.B), Position = 1f },
                        };

                        using var gradBrush = new CanvasLinearGradientBrush(ds, gradStops)
                        {
                            StartPoint = new Vector2(featherX, 0f),
                            EndPoint = new Vector2(featherX + featherActual, 0f),
                        };

                        var featherRect = new Rect(featherX, drawY, featherActual, wordH);

                        using (ds.CreateLayer(gradBrush, featherRect))
                        {
                            ds.DrawText(wl.Text, wordRect, _brightColor, lyricsFmt);
                        }
                    }
                }
            }

            // ── 翻译文字 ─────────────────────────────────────────────
            if (!string.IsNullOrEmpty(TranslateText))
            {
                using var measureFmt = new CanvasTextFormat
                {
                    FontFamily = FontFamilyName,
                    FontSize = (float)LyricsFontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                };
                using var measureLayout = new CanvasTextLayout(
                    ds, BuildFullText(LyricWords!), measureFmt, layoutWidth, 9999f);
                float lyricsHeight = (float)measureLayout.LayoutBounds.Height;

                using var transFmt = new CanvasTextFormat
                {
                    FontFamily = FontFamilyName,
                    FontSize = (float)TranslateFontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = LyricsTextAlignment,
                };

                ds.DrawText(
                    TranslateText,
                    new Rect(
                        RenderPadding,
                        drawOffsetY + PaddingV + lyricsHeight + 6f,
                        layoutWidth,
                        9999f),
                    _translateColor,
                    transFmt);
            }
        }

        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new System.Text.StringBuilder(words.Count * 6);
            foreach (var w in words) sb.Append(w.Word);
            return sb.ToString();
        }
    }
}