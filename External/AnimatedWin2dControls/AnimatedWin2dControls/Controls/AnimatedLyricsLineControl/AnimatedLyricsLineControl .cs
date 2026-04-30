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
        // struct 替代 record class，内存连续，减少堆分配
        private struct WordLayout
        {
            public string Text;
            public float X, Y, FullWidth, Height;
        }

        private WordLayout[] _wordLayouts = [];
        private int _wordLayoutCount = 0;

        private string? _cachedText = null;
        private float _cachedWidth = 0f;
        private float _cachedFontSize = 0f;
        private float _measuredHeight = 0f;

        // 设备未就绪时需要在 CreateResources 后重新 Measure
        private bool _needsRemeasure = false;

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算（Catmull-Rom 风格 + AnimationSmoothness 混合）
        //
        // AnimationSmoothness（0~1）控制全局平滑强度：
        //   0.0 = 严格线性，每字匀速，字边界有速度突变，精确匹配时间轴
        //   0.5 = 均衡（默认），Catmull-Rom 与自然速度各半，误差 ≤50ms
        //   1.0 = 最大平滑，完全 Catmull-Rom 切线，允许 ≤100ms 精度误差
        //
        // 时间轴轻微重分配（timeBias）：
        //   smoothness > 0 时，控制点时间向前偏移 smoothness×bias×duration，
        //   使扫光具有「预判感」，消除字边界机械感。
        //   最大偏移量 = smoothness×0.08×duration，
        //   例：smoothness=1、duration=1000ms → 提前 80ms（≤100ms 约束内）。
        // ══════════════════════════════════════════════════════════════

        // 曲线控制点：struct 数组，内存连续
        private struct CurvePoint
        {
            public double TimeMs;
            public float PixelX;
            public float VelIn;
            public float VelOut;
        }

        private struct RowCurve
        {
            public TimeSpan Origin;
            public CurvePoint[] Points;
            public int Count;
        }

        private RowCurve[] _rowCurves = [];
        private int _rowCurveCount = 0;

        // 视觉行分组缓存（从每帧 DrawContent 移到布局阶段预计算）
        private struct VisualRow
        {
            public float MinX, MaxX, Y, H;
            public int WordStart, WordEnd;
        }

        private VisualRow[] _visualRows = [];
        private int _visualRowCount = 0;

        // ── 独立时钟 ─────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private DateTimeOffset _lastTickAt;
        private TimeSpan _currentTime = TimeSpan.Zero;
        private TimeSpan _lastExternalTime = TimeSpan.Zero;
        private const double SyncThresholdMs = 150.0;

        // ── 平滑追赶：float[] 替代 Dictionary<int,float>，避免装箱 ───
        private float[] _smoothedRevealX = [];
        private const float SmoothLerpSpeed = 18f;
        private const float SmoothMaxPixelsPerSec = 2000f;

        // ── 渲染状态 ─────────────────────────────────────────────────
        private bool _isCurrentLine = false;
        private bool _isPlaying = false;

        // ── 颜色 ─────────────────────────────────────────────────────
        private Color _dimColor = Color.FromArgb(128, 0, 0, 0);
        private Color _brightColor = Color.FromArgb(255, 255, 255, 255);
        private Color _translateColor = Color.FromArgb(155, 255, 255, 255);

        private const float FeatherWidth = 22f;
        private const float PaddingV = 12f;
        private const float RenderPadding = 10f;
        private const float TranslateGapV = 3f;

        // ── CanvasTextFormat 跨帧复用 ─────────────────────────────────
        // CanvasTextFormat 构造涉及 DirectWrite 字体回退树，复用可降低每帧 CPU 开销
        private CanvasTextFormat? _lyricsFmt;
        private CanvasTextFormat? _transFmt;
        private string? _cachedFontFamily;
        private float _cachedLyricsFontSizeForFmt;
        private float _cachedTranslateFontSizeForFmt;
        private CanvasHorizontalAlignment _cachedTransAlignment;

        // ── 渐变 stops 复用（每帧颜色相同，不重建） ──────────────────
        private readonly CanvasGradientStop[] _gradStops = new CanvasGradientStop[2];
        private bool _gradStopsValid = false;

        // ── 翻译行 Y 偏移缓存（避免 DrawContent 每帧重新 layout） ────
        private float _cachedTranslateOffsetY = 0f;

        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

        // ── 构造 ─────────────────────────────────────────────────────
        public AnimatedLyricsLineControl()
        {
            DefaultStyleKey = typeof(AnimatedLyricsLineControl);
            UpdateColors(IsDark);
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

            UpdateColors(IsDark);
            UpdateTimerState();
        }

        // ── MeasureOverride ──────────────────────────────────────────
        protected override Size MeasureOverride(Size availableSize)
        {
            float w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 400f
                : (float)availableSize.Width;

            float fallback = CalcFallbackHeight();

            bool cacheValid =
                _cachedText != null &&
                Math.Abs(_cachedWidth - w) < 1f &&
                _cachedFontSize == (float)LyricsFontSize &&
                _cachedAlignment == LyricsTextAlignment;

            if (cacheValid && _measuredHeight > 0f)
                return new Size(availableSize.Width, _measuredHeight);

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                EnsureLayout(device, w);
                if (_measuredHeight > 0f)
                    return new Size(availableSize.Width, _measuredHeight);
            }
            catch
            {
                _needsRemeasure = true;
                return new Size(availableSize.Width,
                    _measuredHeight > 0f ? _measuredHeight : fallback);
            }

            return new Size(availableSize.Width,
                _measuredHeight > 0f ? _measuredHeight : fallback);
        }

        private float CalcFallbackHeight()
        {
            float lyricLineH = (float)LyricsFontSize * 1.4f;
            float transLineH = string.IsNullOrEmpty(TranslateText)
                ? 0f
                : (float)TranslateFontSize * 1.4f + TranslateGapV;
            return RenderPadding * 2f + PaddingV * 2f + lyricLineH + transLineH;
        }

        // ── 设备重建 ─────────────────────────────────────────────────
        private void OnCreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            DisposeFmtCache();
            InvalidateLayoutCache();
            if (_needsRemeasure)
            {
                _needsRemeasure = false;
                InvalidateMeasure();
            }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只清字符缓存，保留 _measuredHeight 作为过渡高度防止塌缩
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            ResetSmoothedRevealX();
            _rowCurveCount = 0;
            _visualRowCount = 0;
            _canvas?.Invalidate();
            InvalidateMeasure();
        }

        // ══════════════════════════════════════════════════════════════
        // 独立时钟
        // ══════════════════════════════════════════════════════════════

        private void UpdateTimerState()
        {
            if (!_isCurrentLine) { DestroyTimer(); return; }
            if (_timer is null) CreateTimer();
            if (_isPlaying)
            {
                if (!_timer!.IsEnabled) { _lastTickAt = DateTimeOffset.UtcNow; _timer.Start(); }
            }
            else
            {
                _timer!.Stop();
            }
        }

        private void CreateTimer()
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += OnTimerTick;
            _lastTickAt = DateTimeOffset.UtcNow;
        }

        private void DestroyTimer()
        {
            if (_timer is null) return;
            _timer.Stop();
            _timer.Tick -= OnTimerTick;
            _timer = null;
        }

        private void OnTimerTick(object? sender, object e)
        {
            var now = DateTimeOffset.UtcNow;
            var delta = now - _lastTickAt;
            _lastTickAt = now;
            if (delta > TimeSpan.FromSeconds(1)) delta = TimeSpan.FromMilliseconds(16);
            _currentTime += delta;
            _canvas?.Invalidate();
        }

        // ══════════════════════════════════════════════════════════════
        // 依赖属性
        // ══════════════════════════════════════════════════════════════

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
            {
                c._currentTime = TimeSpan.Zero;
                c._lastExternalTime = TimeSpan.Zero;
                c.ResetSmoothedRevealX();
            }
            c.UpdateTimerState();
            c.IsCurrentLineChanged?.Invoke(c, c._isCurrentLine);
            c._canvas?.Invalidate();
        }

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying),
                typeof(bool), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        private static void OnIsPlayingChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c._isPlaying = (bool)e.NewValue;
            if (c._isPlaying && c._timer is not null) c._lastTickAt = DateTimeOffset.UtcNow;
            c.UpdateTimerState();
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
            var externalTime = (TimeSpan)e.NewValue;
            c._lastExternalTime = externalTime;
            if (!c._isCurrentLine) return;

            if (c._timer is null || !c._timer.IsEnabled)
            {
                c._currentTime = externalTime;
                c._canvas?.Invalidate();
                return;
            }

            double diffMs = Math.Abs((externalTime - c._currentTime).TotalMilliseconds);
            if (diffMs > SyncThresholdMs)
            {
                c._currentTime = externalTime;
                c._lastTickAt = DateTimeOffset.UtcNow;
                c.ResetSmoothedRevealX();
                c._canvas?.Invalidate();
            }
        }

        // ── OffsetMs ─────────────────────────────────────────────────
        public static readonly DependencyProperty OffsetMsProperty =
            DependencyProperty.Register(nameof(OffsetMs),
                typeof(double), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(0.0, OnOffsetMsChanged));

        /// <summary>
        /// 全局动画偏移（毫秒）。正值延后，负值提前。仅影响渲染查询时间。
        /// </summary>
        public double OffsetMs
        {
            get => (double)GetValue(OffsetMsProperty);
            set => SetValue(OffsetMsProperty, value);
        }

        private static void OnOffsetMsChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c._canvas?.Invalidate();
        }

        // ── AnimationSmoothness ───────────────────────────────────────
        public static readonly DependencyProperty AnimationSmoothnessProperty =
            DependencyProperty.Register(nameof(AnimationSmoothness),
                typeof(double), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(0.65, OnAnimationSmoothnessChanged));

        /// <summary>
        /// 逐字扫光动画的平滑强度，范围 [0, 1]。
        ///
        /// 0.0 — 严格线性：每个字匀速扫过，字边界处速度可能突变，最精确。
        /// 0.65 — 默认均衡：Catmull-Rom 切线与自然速度按比例混合，
        ///         过渡流畅，精度误差 ≤65ms。
        /// 1.0 — 最大平滑：切线完全由相邻字速度决定，整体最连续，
        ///         允许 ≤100ms 精度误差。
        ///
        /// 修改后自动重建曲线，不需要重新布局。
        /// </summary>
        public double AnimationSmoothness
        {
            get => (double)GetValue(AnimationSmoothnessProperty);
            set => SetValue(AnimationSmoothnessProperty, Math.Clamp(value, 0.0, 1.0));
        }

        private static void OnAnimationSmoothnessChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            // 只重建曲线，不重建布局，不触发 Measure
            c._rowCurveCount = 0;
            if (c._wordLayoutCount > 0 && c.LyricWords is { } words)
                c.BuildRowCurves(words);
            c._canvas?.Invalidate();
        }

        // ── 其余布局属性 ──────────────────────────────────────────────

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
            c._gradStopsValid = false;
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
            _gradStopsValid = false;
        }

        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
            c.InvalidateMeasure();
        }

        // ══════════════════════════════════════════════════════════════
        // 缓存管理
        // ══════════════════════════════════════════════════════════════

        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            // 不清零 _measuredHeight，保留过渡高度防止布局塌缩
            ResetSmoothedRevealX();
            _rowCurveCount = 0;
            _visualRowCount = 0;
            DisposeFmtCache();
            _canvas?.Invalidate();
        }

        /// <summary>将平滑追赶数组内容置为 NaN（标记"未初始化"）。</summary>
        private void ResetSmoothedRevealX()
        {
            for (int i = 0; i < _smoothedRevealX.Length; i++)
                _smoothedRevealX[i] = float.NaN;
        }

        private void DisposeFmtCache()
        {
            _lyricsFmt?.Dispose();
            _lyricsFmt = null;
            _transFmt?.Dispose();
            _transFmt = null;
            _cachedFontFamily = null;
        }

        // ══════════════════════════════════════════════════════════════
        // CanvasTextFormat 跨帧复用
        // ══════════════════════════════════════════════════════════════

        private CanvasTextFormat GetLyricsFmt()
        {
            float fontSize = (float)LyricsFontSize;
            string family = FontFamilyName;
            if (_lyricsFmt is null ||
                _cachedFontFamily != family ||
                _cachedLyricsFontSizeForFmt != fontSize)
            {
                _lyricsFmt?.Dispose();
                _lyricsFmt = new CanvasTextFormat
                {
                    FontFamily = family,
                    FontSize = fontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                };
                _cachedLyricsFontSizeForFmt = fontSize;
                // family 由 GetTransFmt 统一写入 _cachedFontFamily
            }
            return _lyricsFmt;
        }

        private CanvasTextFormat GetTransFmt()
        {
            float fontSize = (float)TranslateFontSize;
            string family = FontFamilyName;
            var align = LyricsTextAlignment;
            if (_transFmt is null ||
                _cachedFontFamily != family ||
                _cachedTranslateFontSizeForFmt != fontSize ||
                _cachedTransAlignment != align)
            {
                _transFmt?.Dispose();
                _transFmt = new CanvasTextFormat
                {
                    FontFamily = family,
                    FontSize = fontSize,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = align,
                };
                _cachedTranslateFontSizeForFmt = fontSize;
                _cachedTransAlignment = align;
                _cachedFontFamily = family; // 两个 fmt 都已同步，统一写
            }
            return _transFmt;
        }

        // ══════════════════════════════════════════════════════════════
        // 布局计算
        // ══════════════════════════════════════════════════════════════

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

            _wordLayoutCount = 0;
            _rowCurveCount = 0;
            _visualRowCount = 0;
            ResetSmoothedRevealX();

            float layoutWidth = Math.Max(1f, availableWidth - RenderPadding * 2f);

            // EnsureLayout 直接构造 fmt（可能在 Measure 路径调用，fmt 缓存未就绪）
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

            // 按需扩容（避免频繁重分配）
            if (_wordLayouts.Length < words.Count)
                _wordLayouts = new WordLayout[words.Count + 8];

            int charOffset = 0;
            for (int i = 0; i < words.Count; i++)
            {
                string wt = words[i].Word;
                int len = Math.Max(wt.Length, 1);
                var regions = layout.GetCharacterRegions(charOffset, len);

                if (regions.Length > 0)
                {
                    Rect first = regions[0].LayoutBounds;
                    Rect last = regions[^1].LayoutBounds;
                    float fw = (float)(last.Right - first.Left);
                    float h = (float)first.Height;
                    _wordLayouts[i] = fw > 0 && h > 0
                        ? new WordLayout
                        {
                            Text = wt,
                            X = (float)first.Left + alignOffsetX + RenderPadding,
                            Y = (float)first.Top,
                            FullWidth = fw,
                            Height = h,
                        }
                        : new WordLayout { Text = wt, X = RenderPadding };
                }
                else
                {
                    _wordLayouts[i] = new WordLayout { Text = wt, X = RenderPadding };
                }

                charOffset += wt.Length;
            }
            _wordLayoutCount = words.Count;

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
                translateHeight = (float)transLayout.LayoutBounds.Height + TranslateGapV;
            }

            _cachedTranslateOffsetY = PaddingV + lyricsHeight + TranslateGapV;
            float totalHeight = RenderPadding + PaddingV
                              + lyricsHeight
                              + translateHeight          // = transLayoutBounds.Height + TranslateGapV，无翻译时 = 0
                              + PaddingV + RenderPadding;
            _measuredHeight = Math.Max(totalHeight, CalcFallbackHeight());

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;

            // 布局变了，fmt 缓存作废
            DisposeFmtCache();

            BuildVisualRows();
            BuildRowCurves(words);
            EnsureSmoothedRevealXCapacity(_visualRowCount);
        }

        private void EnsureLayoutForDraw(ICanvasResourceCreator creator, float actualWidth)
        {
            var words = LyricWords;
            if (words is null || words.Count == 0) return;

            string fullText = BuildFullText(words);
            float fontSize = (float)LyricsFontSize;
            var alignment = LyricsTextAlignment;

            bool needRebuild =
                fullText != _cachedText ||
                Math.Abs(actualWidth - _cachedWidth) >= 2f ||
                fontSize != _cachedFontSize ||
                alignment != _cachedAlignment;

            if (!needRebuild) return;

            float oldHeight = _measuredHeight;
            EnsureLayout(creator, actualWidth);

            if (Math.Abs(_measuredHeight - oldHeight) > 1f)
                DispatcherQueue.TryEnqueue(InvalidateMeasure);
        }

        private void EnsureSmoothedRevealXCapacity(int rowCount)
        {
            if (_smoothedRevealX.Length >= rowCount) return;
            _smoothedRevealX = new float[rowCount + 2];
            for (int i = 0; i < _smoothedRevealX.Length; i++)
                _smoothedRevealX[i] = float.NaN;
        }

        // ══════════════════════════════════════════════════════════════
        // 视觉行预计算
        // ══════════════════════════════════════════════════════════════

        private void BuildVisualRows()
        {
            _visualRowCount = 0;
            int count = _wordLayoutCount;
            if (count == 0) return;

            int estimatedRows = Math.Max(4, count / 4);
            if (_visualRows.Length < estimatedRows)
                _visualRows = new VisualRow[estimatedRows + 2];

            int rs = 0;
            while (rs < count)
            {
                while (rs < count && _wordLayouts[rs].FullWidth <= 0) rs++;
                if (rs >= count) break;

                ref readonly var first = ref _wordLayouts[rs];
                float ry = first.Y, rh = first.Height;
                float mnX = first.X, mxX = first.X + first.FullWidth;
                int re = rs + 1;

                while (re < count)
                {
                    ref readonly var wl = ref _wordLayouts[re];
                    if (wl.FullWidth <= 0) { re++; continue; }
                    if (Math.Abs(wl.Y - ry) > rh * 0.5f) break;
                    if (wl.X < mnX) mnX = wl.X;
                    if (wl.X + wl.FullWidth > mxX) mxX = wl.X + wl.FullWidth;
                    re++;
                }

                if (_visualRowCount >= _visualRows.Length)
                {
                    var tmp = new VisualRow[_visualRows.Length * 2 + 2];
                    Array.Copy(_visualRows, tmp, _visualRows.Length);
                    _visualRows = tmp;
                }

                _visualRows[_visualRowCount++] = new VisualRow
                {
                    MinX = mnX,
                    MaxX = mxX,
                    Y = ry,
                    H = rh,
                    WordStart = rs,
                    WordEnd = re - 1,
                };
                rs = re;
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算
        // ══════════════════════════════════════════════════════════════

        private void BuildRowCurves(IList<LyricWord> words)
        {
            _rowCurveCount = 0;
            int count = _wordLayoutCount;
            if (count == 0) return;

            double smoothness = Math.Clamp(AnimationSmoothness, 0.0, 1.0);

            int rowStart = 0;
            while (rowStart < count)
            {
                while (rowStart < count && _wordLayouts[rowStart].FullWidth <= 0)
                    rowStart++;
                if (rowStart >= count) break;

                ref readonly var firstWl = ref _wordLayouts[rowStart];
                float rowY = firstWl.Y, rowH = firstWl.Height;
                float minX = firstWl.X, maxX = firstWl.X + firstWl.FullWidth;
                int rowEnd = rowStart + 1;

                while (rowEnd < count)
                {
                    ref readonly var wl = ref _wordLayouts[rowEnd];
                    if (wl.FullWidth <= 0) { rowEnd++; continue; }
                    if (Math.Abs(wl.Y - rowY) > rowH * 0.5f) break;
                    if (wl.X < minX) minX = wl.X;
                    if (wl.X + wl.FullWidth > maxX) maxX = wl.X + wl.FullWidth;
                    rowEnd++;
                }

                int fv = rowStart;
                while (fv < rowEnd && _wordLayouts[fv].FullWidth <= 0) fv++;
                int lv = rowEnd - 1;
                while (lv >= rowStart && _wordLayouts[lv].FullWidth <= 0) lv--;

                if (_rowCurveCount >= _rowCurves.Length)
                {
                    var tmp = new RowCurve[_rowCurves.Length * 2 + 4];
                    Array.Copy(_rowCurves, tmp, _rowCurves.Length);
                    _rowCurves = tmp;
                }

                _rowCurves[_rowCurveCount++] = fv <= lv
                    ? BuildSingleRowCurve(words, fv, lv, minX, maxX, smoothness)
                    : new RowCurve { Origin = TimeSpan.Zero, Points = [], Count = 0 };

                rowStart = rowEnd;
            }
        }

        private RowCurve BuildSingleRowCurve(
            IList<LyricWord> words,
            int firstValid, int lastValid,
            float minX, float maxX,
            double smoothness)
        {
            int maxN = lastValid - firstValid + 1;

            // stackalloc 用于短行（≤64字），避免堆分配
            Span<int> indicesBuf = maxN <= 64 ? stackalloc int[maxN] : new int[maxN];
            Span<float> pxWidthsBuf = maxN <= 64 ? stackalloc float[maxN] : new float[maxN];
            Span<double> durMsBuf = maxN <= 64 ? stackalloc double[maxN] : new double[maxN];

            int n = 0;
            for (int i = firstValid; i <= lastValid; i++)
            {
                if (_wordLayouts[i].FullWidth <= 0) continue;
                indicesBuf[n] = i;
                pxWidthsBuf[n] = _wordLayouts[i].FullWidth;
                durMsBuf[n] = Math.Max(words[i].Duration.TotalMilliseconds, 1.0);
                n++;
            }

            if (n == 0)
                return new RowCurve { Origin = words[firstValid].StartTime, Points = [], Count = 0 };

            // ── 自然速度 ──────────────────────────────────────────────
            Span<double> natVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++)
                natVel[k] = pxWidthsBuf[k] / durMsBuf[k];

            // ── Catmull-Rom 切线速度 ──────────────────────────────────
            //
            // 核心公式：对字 k，使用前后邻字的时间加权平均速度作为切线：
            //   catmullVel[k] = (natVel[k-1]×dur[k+1] + natVel[k+1]×dur[k-1])
            //                   / (dur[k-1] + dur[k+1])
            //
            // 权重选择原因：持续时间越长的字，其速度对邻字的"视觉拉力"越小，
            // 因此用对端时长作权重，对时长差异大的相邻字更鲁棒。
            //
            // 端点退化为单侧平均（前端取后邻，末端取前邻）。
            Span<double> catmullVel = n <= 64 ? stackalloc double[n] : new double[n];
            if (n == 1)
            {
                catmullVel[0] = natVel[0];
            }
            else
            {
                catmullVel[0] = (natVel[0] + natVel[1]) * 0.5;
                catmullVel[n - 1] = (natVel[n - 2] + natVel[n - 1]) * 0.5;
                for (int k = 1; k < n - 1; k++)
                {
                    double tPrev = durMsBuf[k - 1];
                    double tNext = durMsBuf[k + 1];
                    catmullVel[k] = (natVel[k - 1] * tNext + natVel[k + 1] * tPrev)
                                    / (tPrev + tNext);
                }
            }

            // ── 防过冲 clamp（非负，且不超自然速度 1.8 倍） ───────────
            const double maxRatio = 1.8;
            for (int k = 0; k < n; k++)
                catmullVel[k] = Math.Clamp(catmullVel[k], 0.0, natVel[k] * maxRatio);

            // ── 按 smoothness 混合：finalVel = lerp(natVel, catmullVel, s) ──
            Span<double> finalVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++)
                finalVel[k] = natVel[k] + smoothness * (catmullVel[k] - natVel[k]);

            // ── 构建控制点（含时间重心偏移） ───────────────────────────
            //
            // 时间偏移：adjustedTime = origTime - smoothness × timeBias × duration
            //   timeBias = 0.08，smoothness=1 时最大提前 8%×duration（≤100ms约束内）。
            //   偏移方向为负（提前），使扫光在字实际开始前已有预判感。
            //   严格保证调整后时间不早于前一个控制点（单调递增约束）。
            const double timeBias = 0.08;
            var lineOrigin = words[firstValid].StartTime;
            var points = new CurvePoint[n + 1];

            for (int k = 0; k < n; k++)
            {
                int wi = indicesBuf[k];
                double origTMs = (words[wi].StartTime - lineOrigin).TotalMilliseconds;
                double adjustedTMs = origTMs - smoothness * timeBias * durMsBuf[k];
                double minTMs = k == 0 ? 0.0 : points[k - 1].TimeMs + 1.0;
                adjustedTMs = Math.Max(adjustedTMs, minTMs);

                float pixelX = _wordLayouts[wi].X - minX;
                float vel = (float)finalVel[k];
                points[k] = new CurvePoint
                {
                    TimeMs = adjustedTMs,
                    PixelX = pixelX,
                    VelIn = vel,
                    VelOut = vel,
                };
            }

            // 末尾哨兵：严格对齐最后一字结束时间，不偏移
            {
                int lastIdx = indicesBuf[n - 1];
                double endMs = (words[lastIdx].StartTime + words[lastIdx].Duration - lineOrigin)
                               .TotalMilliseconds;
                endMs = Math.Max(endMs, points[n - 1].TimeMs + 1.0);
                float endVel = (float)natVel[n - 1];
                points[n] = new CurvePoint
                {
                    TimeMs = endMs,
                    PixelX = maxX - minX,
                    VelIn = endVel,
                    VelOut = endVel,
                };
            }

            return new RowCurve { Origin = lineOrigin, Points = points, Count = n + 1 };
        }

        // ══════════════════════════════════════════════════════════════
        // 核心绘制
        // ══════════════════════════════════════════════════════════════

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            var words = LyricWords;
            if (words is null || words.Count == 0) return;

            float w = (float)sender.ActualWidth;
            if (w <= 0) return;

            EnsureLayoutForDraw(ds, w);
            if (_wordLayoutCount == 0) return;

            using var cl = new CanvasCommandList(ds);
            using (var clDs = cl.CreateDrawingSession())
                DrawContent(clDs, words, w);

            float blurAmount = _isCurrentLine ? 0f : 1.25f;
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

        private void DrawContent(CanvasDrawingSession ds,
            IList<LyricWord> words, float totalWidth)
        {
            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Auto;

            float layoutWidth = Math.Max(1f, totalWidth - RenderPadding * 2f);
            float drawOffsetY = RenderPadding;

            // 复用 CanvasTextFormat，不再每帧 new
            var lyricsFmt = GetLyricsFmt();

            int count = _wordLayoutCount;
            if (count == 0) return;

            // ── 暗色底层 ──────────────────────────────────────────────
            for (int i = 0; i < count; i++)
            {
                ref readonly var wl = ref _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                ds.DrawText(wl.Text,
                    new Rect(wl.X, wl.Y + drawOffsetY + PaddingV, wl.FullWidth, wl.Height),
                    _dimColor, lyricsFmt);
            }

            if (!_isCurrentLine) goto DrawTranslate;

            // ── 使用预计算视觉行，无每帧分组开销 ─────────────────────
            EnsureSmoothedRevealXCapacity(_visualRowCount);

            var effectiveTime = _currentTime - TimeSpan.FromMilliseconds(OffsetMs);
            const float kDeltaSec = 0.016f;

            for (int ri = 0; ri < _visualRowCount; ri++)
            {
                ref readonly var row = ref _visualRows[ri];
                float minX = row.MinX, maxX = row.MaxX;
                float rowY = row.Y, rowH = row.H;
                if (maxX - minX <= 0) continue;

                int wordStart = row.WordStart, wordEnd = row.WordEnd;
                int fv = wordStart;
                while (fv <= wordEnd && _wordLayouts[fv].FullWidth <= 0) fv++;
                int lv = wordEnd;
                while (lv >= wordStart && _wordLayouts[lv].FullWidth <= 0) lv--;
                if (fv > lv) continue;

                float targetRevealX = ri < _rowCurveCount && _rowCurves[ri].Count > 0
                    ? CalcRevealXHermite(ref _rowCurves[ri], effectiveTime, minX, maxX)
                    : CalcRevealXFallback(words, fv, lv, minX, effectiveTime);

                // 指数衰减 lerp（NaN = 首次，直接吸附）
                float smoothed = _smoothedRevealX[ri];
                if (float.IsNaN(smoothed)) smoothed = targetRevealX;

                float diff = targetRevealX - smoothed;
                if (Math.Abs(diff) > 0.5f)
                {
                    float lf = 1f - MathF.Exp(-SmoothLerpSpeed * kDeltaSec);
                    float step = diff * lf;
                    float maxStep = SmoothMaxPixelsPerSec * kDeltaSec;
                    if (Math.Abs(step) > maxStep) step = Math.Sign(step) * maxStep;
                    smoothed += step;
                    if (Math.Abs(targetRevealX - smoothed) < 0.5f) smoothed = targetRevealX;
                }
                else
                {
                    smoothed = targetRevealX;
                }

                _smoothedRevealX[ri] = smoothed;
                float revealX = smoothed;
                float highlightWidth = revealX - minX;

                // ── 亮色高亮 ──────────────────────────────────────────
                if (highlightWidth > 0f)
                {
                    using (ds.CreateLayer(1f,
                        new Rect(minX, rowY + drawOffsetY + PaddingV, highlightWidth, rowH)))
                    {
                        for (int i = fv; i <= lv; i++)
                        {
                            ref readonly var wl = ref _wordLayouts[i];
                            if (wl.FullWidth <= 0) continue;
                            ds.DrawText(wl.Text,
                                new Rect(wl.X, wl.Y + drawOffsetY + PaddingV,
                                         wl.FullWidth, wl.Height),
                                _brightColor, lyricsFmt);
                        }
                    }
                }

                // ── 羽化边缘 ──────────────────────────────────────────
                if (revealX > minX && revealX < maxX)
                {
                    float feather = Math.Min(FeatherWidth, maxX - revealX);
                    if (feather > 0.5f)
                    {
                        // 复用 gradStops 数组，不每帧分配
                        if (!_gradStopsValid)
                        {
                            _gradStops[0] = new CanvasGradientStop
                            { Color = _brightColor, Position = 0f };
                            _gradStops[1] = new CanvasGradientStop
                            {
                                Color = Color.FromArgb(0,
                                    _brightColor.R, _brightColor.G, _brightColor.B),
                                Position = 1f,
                            };
                            _gradStopsValid = true;
                        }

                        using var gradBrush = new CanvasLinearGradientBrush(ds, _gradStops)
                        {
                            StartPoint = new Vector2(revealX, 0f),
                            EndPoint = new Vector2(revealX + feather, 0f),
                        };
                        using (ds.CreateLayer(gradBrush,
                            new Rect(revealX, rowY + drawOffsetY + PaddingV, feather, rowH)))
                        {
                            for (int i = fv; i <= lv; i++)
                            {
                                ref readonly var wl = ref _wordLayouts[i];
                                if (wl.FullWidth <= 0) continue;
                                ds.DrawText(wl.Text,
                                    new Rect(wl.X, wl.Y + drawOffsetY + PaddingV,
                                             wl.FullWidth, wl.Height),
                                    _brightColor, lyricsFmt);
                            }
                        }
                    }
                }
            }

        DrawTranslate:
            if (!string.IsNullOrEmpty(TranslateText))
            {
                // 复用 transFmt + 缓存的 Y 偏移，不再每帧重新 layout
                var transFmt = GetTransFmt();
                ds.DrawText(
                    TranslateText,
                    new Rect(RenderPadding,
                             drawOffsetY + _cachedTranslateOffsetY,
                             layoutWidth, 9999f),
                    _translateColor, transFmt);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 三次 Hermite 样条插值
        // ══════════════════════════════════════════════════════════════

        private static float HermiteInterp(
            float p0, float p1,
            float m0, float m1,
            double dt, double elapsed)
        {
            if (dt <= 0) return p1;
            float t = Math.Clamp((float)(elapsed / dt), 0f, 1f);
            float t2 = t * t, t3 = t2 * t;
            float tm0 = m0 * (float)dt;
            float tm1 = m1 * (float)dt;
            return (2 * t3 - 3 * t2 + 1) * p0
                 + (t3 - 2 * t2 + t) * tm0
                 + (-2 * t3 + 3 * t2) * p1
                 + (t3 - t2) * tm1;
        }

        private static float CalcRevealXHermite(
            ref RowCurve curve, TimeSpan effectiveTime, float minX, float maxX)
        {
            int ptCount = curve.Count;
            if (ptCount == 0) return minX;
            var pts = curve.Points;

            double elapsedMs = (effectiveTime - curve.Origin).TotalMilliseconds;
            if (elapsedMs <= pts[0].TimeMs) return minX;
            if (elapsedMs >= pts[ptCount - 1].TimeMs) return maxX;

            int lo = 0, hi = ptCount - 2;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (pts[mid + 1].TimeMs <= elapsedMs) lo = mid + 1;
                else hi = mid;
            }

            ref readonly var p0 = ref pts[lo];
            ref readonly var p1 = ref pts[lo + 1];
            double dt = p1.TimeMs - p0.TimeMs;
            double segElapsed = elapsedMs - p0.TimeMs;

            float px = HermiteInterp(p0.PixelX, p1.PixelX,
                                     p0.VelOut, p1.VelIn,
                                     dt, segElapsed);

            px = Math.Clamp(px,
                Math.Min(p0.PixelX, p1.PixelX),
                Math.Max(p0.PixelX, p1.PixelX));

            return minX + px;
        }

        private float CalcRevealXFallback(
            IList<LyricWord> words, int fv, int lv,
            float minX, TimeSpan effectiveTime)
        {
            if (effectiveTime <= words[fv].StartTime) return minX;

            float totalW = 0f;
            for (int i = fv; i <= lv; i++)
                if (_wordLayouts[i].FullWidth > 0) totalW += _wordLayouts[i].FullWidth;

            if (effectiveTime >= words[lv].StartTime + words[lv].Duration)
                return minX + totalW;

            float accX = minX;
            for (int i = fv; i <= lv; i++)
            {
                ref readonly var wl = ref _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                var word = words[i];
                var wordEnd = word.StartTime + word.Duration;
                if (effectiveTime >= wordEnd)
                {
                    accX += wl.FullWidth;
                }
                else if (effectiveTime >= word.StartTime)
                {
                    float t = word.Duration > TimeSpan.Zero
                        ? Math.Clamp((float)((effectiveTime - word.StartTime).TotalMilliseconds
                                             / word.Duration.TotalMilliseconds), 0f, 1f)
                        : 1f;
                    float t2 = t * t;
                    accX += wl.FullWidth * (t2 * (3f - 2f * t));
                    break;
                }
                else break;
            }
            return accX;
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