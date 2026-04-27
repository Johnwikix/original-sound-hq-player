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
        private float _measuredHeight = 0f; // ★ 新增：MeasureOverride 用的高度

        // ── 渲染状态 ─────────────────────────────────────────────────
        private TimeSpan _currentTime = TimeSpan.Zero;
        private bool _isCurrentLine = false;

        // ── 颜色（改为实例字段，支持亮暗切换） ───────────────────────
        private Color _dimColor = Color.FromArgb(128, 0, 0, 0);       // 暗色模式默认先给亮色
        private Color _brightColor = Color.FromArgb(255, 255, 255, 255);
        private Color _translateColor = Color.FromArgb(155, 255, 255, 255);

        private const float FeatherWidth = 22f;
        private const float PaddingV = 12f;
        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

        public AnimatedLyricsLineControl()
        {
            DefaultStyleKey = typeof(AnimatedLyricsLineControl);
            // 初始化颜色
            UpdateColors(true); // 默认亮色主题（IsDark=false）
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

        // ── MeasureOverride ──────────────────────────────────────────────
        protected override Size MeasureOverride(Size availableSize)
        {
            float w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 400f
                : (float)availableSize.Width;

            // 如果缓存宽度和当前一致，直接用已有结果
            if (_measuredHeight > 0f && Math.Abs(_cachedWidth - w) < 1f)
                return new Size(availableSize.Width, _measuredHeight);

            // 用共享设备做一次完整 EnsureLayout（会更新 _measuredHeight）
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

        // ── 纯高度测量（不依赖 CanvasControl）★ ─────────────────────
        private float MeasureHeight(ICanvasResourceCreator creator, float availableWidth)
        {
            var words = LyricWords;
            if (words is null || words.Count == 0) return 0f;

            string fullText = BuildFullText(words);
            float fontSize = (float)LyricsFontSize;

            using var fmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = fontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };
            using var layout = new CanvasTextLayout(creator, fullText, fmt, availableWidth, 9999f);
            float lyricsBottom = (float)layout.LayoutBounds.Height;

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
                using var transLayout = new CanvasTextLayout(creator, TranslateText, transFmt, availableWidth, 9999f);
                translateHeight = (float)transLayout.LayoutBounds.Height + 6f;
            }

            return PaddingV + lyricsBottom + translateHeight + PaddingV;
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
                new PropertyMetadata(null, OnLayoutPropertyChanged)); // ★ 改为 Layout

        public IList<LyricWord>? LyricWords
        {
            get => (IList<LyricWord>?)GetValue(LyricWordsProperty);
            set => SetValue(LyricWordsProperty, value);
        }

        public static readonly DependencyProperty TranslateTextProperty =
            DependencyProperty.Register(nameof(TranslateText),
                typeof(string), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(string.Empty, OnLayoutPropertyChanged)); // ★ 改为 Layout

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

        // ── IsDark 依赖属性 ★ ────────────────────────────────────────
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

            // 依赖属性回调已在 UI 线程，直接操作安全
            c.UpdateColors((bool)e.NewValue);
            c._canvas?.Invalidate();
        }

        /// <summary>
        /// 根据亮暗主题更新颜色字段。
        /// 只在 UI 线程调用（依赖属性回调 / 构造函数）。
        /// 如需从非 UI 线程调用，请通过 DispatcherQueue.TryEnqueue 包裹。
        /// </summary>
        private void UpdateColors(bool isDark)
        {
            if (isDark)
            {
                // 深色背景：文字用白色
                _dimColor = Color.FromArgb(128, 255, 255, 255);
                _brightColor = Color.FromArgb(255, 255, 255, 255);
                _translateColor = Color.FromArgb(155, 255, 255, 255);
            }
            else
            {
                // 浅色背景：文字用黑色
                _dimColor = Color.FromArgb(100, 0, 0, 0);
                _brightColor = Color.FromArgb(255, 0, 0, 0);
                _translateColor = Color.FromArgb(155, 0, 0, 0);
            }
        }

        // ── 属性变化回调 ─────────────────────────────────────────────

        private static void OnVisualPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
        }

        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
            // ★ 通知 XAML 布局系统重新测量
            c.InvalidateMeasure();
        }

        // ── 布局缓存 ─────────────────────────────────────────────────
        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            _measuredHeight = 0f; // ★ 同时重置 MeasureOverride 缓存
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
                Math.Abs(availableWidth - _cachedWidth) < 1f &&   // ★ 浮点比较改用容差
                fontSize == _cachedFontSize &&
                alignment == _cachedAlignment)
                return;

            _wordLayouts.Clear();

            using var fmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = fontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            using var layout = new CanvasTextLayout(creator, fullText, fmt, availableWidth, 9999f);

            float lineWidth = (float)layout.LayoutBounds.Width;
            float alignOffsetX = alignment switch
            {
                CanvasHorizontalAlignment.Center => (availableWidth - lineWidth) / 2f,
                CanvasHorizontalAlignment.Right => availableWidth - lineWidth,
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

                    _wordLayouts.Add(fw > 0 && h > 0
                        ? new WordLayout(w, (float)first.Left + alignOffsetX, (float)first.Top, fw, h)
                        : new WordLayout(w, 0, 0, 0, 0));
                }
                else
                {
                    _wordLayouts.Add(new WordLayout(w, 0, 0, 0, 0));
                }

                charOffset += w.Length;
            }

            // ★ 统一用 LayoutBounds 算歌词底部，和 CanvasTextLayout 内部一致
            //   不再遍历 _wordLayouts，避免多行时因为词布局偏差导致高度偏小
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
                    creator, TranslateText, transFmt, availableWidth, 9999f);
                // ★ 用 DrawBounds 而非 LayoutBounds，包含实际像素范围（descender 等）
                translateHeight = (float)transLayout.DrawBounds.Height + 6f;
            }

            float totalHeight = PaddingV + lyricsHeight + translateHeight + PaddingV;

            // ★ 所有高度赋值都在这里，MeasureOverride 直接读 _measuredHeight
            _canvasHeight = totalHeight;
            _measuredHeight = totalHeight;

            if (_canvas is not null) _canvas.Height = totalHeight;
            Height = totalHeight;

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;
        }

        // ── 核心绘制 ─────────────────────────────────────────────────────
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
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            int count = Math.Min(words.Count, _wordLayouts.Count);

            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                var word = words[i];
                if (wl.FullWidth <= 0) continue;

                var wordRect = new Rect(wl.X, wl.Y + PaddingV, wl.FullWidth, wl.Height);

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
                float drawY = (float)(wl.Y + PaddingV);
                float wordH = wl.Height;

                // ── 1. 发光层（GaussianBlur on CanvasCommandList）──────────
                DrawGlowForWord(ds, wl, wordRect, lyricsFmt, revealWidth, progress);

                // ── 2. 实心亮色高亮区域 ───────────────────────────────────
                using (ds.CreateLayer(1f, new Rect(wl.X, drawY, revealWidth, wordH)))
                {
                    ds.DrawText(wl.Text, wordRect, _brightColor, lyricsFmt);
                }

                // ── 3. 羽化边缘（LinearGradientBrush 遮罩）───────────────
                if (progress < 1f)
                {
                    float featherActual = Math.Min(FeatherWidth, wl.FullWidth - revealWidth);
                    if (featherActual > 0.5f)
                    {
                        float featherX = (float)(wl.X + revealWidth);

                        // 渐变：左侧不透明 → 右侧完全透明，掩盖亮色文字边缘
                        var gradStops = new CanvasGradientStop[]
                        {
                            new() { Color = _brightColor,Position = 0f },
                            new() { Color = Color.FromArgb(0, _brightColor.R,
                                                      _brightColor.G,
                                                      _brightColor.B), Position = 1f },
                        };

                        using var gradBrush = new CanvasLinearGradientBrush(ds, gradStops)
                        {
                            StartPoint = new Vector2(featherX, 0f),
                            EndPoint = new Vector2(featherX + featherActual, 0f),
                        };

                        var featherRect = new Rect(featherX, drawY, featherActual, wordH);

                        // CreateLayer 接受 ICanvasBrush 作为 opacity mask
                        using (ds.CreateLayer(gradBrush, featherRect))
                        {
                            ds.DrawText(wl.Text, wordRect, _brightColor, lyricsFmt);
                        }
                    }
                }
            }

            // ── 翻译文字 ─────────────────────────────────────────────────
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
                using var measureLayout = new CanvasTextLayout(ds, BuildFullText(LyricWords!), measureFmt, w, 9999f);
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
                    new Rect(0, PaddingV + lyricsHeight + 6f, w, 9999f),
                    _translateColor,
                    transFmt);
            }
        }

        // ── 发光效果辅助方法 ─────────────────────────────────────────────
        /// <summary>
        /// 用 CanvasCommandList + GaussianBlurEffect 为已点亮区域绘制发光。
        /// 发光区域随 progress 扩展，并在右侧用线性渐变淡出，与羽化边缘视觉一致。
        /// </summary>
        private void DrawGlowForWord(
            CanvasDrawingSession ds,
            WordLayout wl,
            Rect wordRect,
            CanvasTextFormat fmt,
            float revealWidth,
            float progress)
        {
            // 发光颜色：在亮色基础上增加高 alpha 的半透明覆盖
            Color glowColor = Color.FromArgb(
                180,
                _brightColor.R,
                _brightColor.G,
                _brightColor.B);

            float drawY = (float)(wl.Y + PaddingV);
            float wordH = wl.Height;

            // ── Step 1：把文字画到 CommandList ────────────────────────────
            using var cl = new CanvasCommandList(ds);
            using (var clDs = cl.CreateDrawingSession())
            {
                clDs.DrawText(wl.Text, wordRect, glowColor, fmt);
            }

            // ── Step 2：GaussianBlur ──────────────────────────────────────
            var blur = new Microsoft.Graphics.Canvas.Effects.GaussianBlurEffect
            {
                Source = cl,
                BlurAmount = 8f,         // 模糊半径，可按需调整
                BorderMode = Microsoft.Graphics.Canvas.Effects.EffectBorderMode.Soft,
            };

            // ── Step 3：裁剪到已点亮区域，右侧用渐变淡出 ─────────────────
            // 发光范围比实际文字略宽（向右多出 FeatherWidth），但透明度在边缘渐变
            float glowRight = (float)(wl.X + revealWidth + FeatherWidth);
            float glowLeft = (float)wl.X;

            // 裁剪到不超过词边界
            float clipRight = Math.Min(glowRight, (float)(wl.X + wl.FullWidth));

            var glowStops = new CanvasGradientStop[]
            {
                new() { Color = Color.FromArgb(255, 255, 255, 255), Position = 0f },
                new() { Color = Color.FromArgb(255, 255, 255, 255),
                        Position = progress < 1f ? revealWidth / wl.FullWidth * 0.85f : 1f },
                new() { Color = Color.FromArgb(0,   255, 255, 255), Position = 1f },
            };

            using var glowMask = new CanvasLinearGradientBrush(ds, glowStops)
            {
                StartPoint = new Vector2(glowLeft, 0f),
                EndPoint = new Vector2(clipRight, 0f),
            };

            // 发光垂直方向稍微扩展，让光晕更可见
            float glowPadding = wordH * 0.25f;
            var glowRect = new Rect(
                glowLeft,
                drawY - glowPadding,
                clipRight - glowLeft,
                wordH + glowPadding * 2f);

            using (ds.CreateLayer(glowMask, glowRect))
            {
                ds.DrawImage(blur);
            }

            blur.Dispose();
        }

        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new System.Text.StringBuilder(words.Count * 6);
            foreach (var w in words) sb.Append(w.Word);
            return sb.ToString();
        }
    }
}