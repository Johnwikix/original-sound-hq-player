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
        private float _measuredHeight = 0f;

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算（三次 Hermite 样条）
        //
        // 设计原则：精确优先，平滑在精确约束下尽力而为。
        //
        // 精确约束（不可违反）：
        //   控制点时间 = 原始 StartTime（一律不调整）
        //   控制点像素 = 真实布局 X（一律不移动）
        //   => 扫光在每个字的 StartTime 时刻，严格位于该字的像素起点
        //
        // 平滑策略（Hermite 切线）：
        //   每个字的自然速度 = FullWidth / Duration (px/ms)
        //   对自然速度做加权移动平均 → 平滑速度，用作两端切线
        //   平滑速度只影响字与字之间的过渡曲线形状，不影响控制点本身
        //   => 相邻字速度差异大时，扫光在字边界处平滑加/减速，无突变
        //
        // 防过冲：
        //   切线速度上限 = 自然速度 × MaxTangentRatio
        //   插值结果 clamp 到 [p0.PixelX, p1.PixelX]
        // ══════════════════════════════════════════════════════════════

        private sealed record CurvePoint(
            double TimeMs,  // 相对于行首 StartTime 的 ms 偏移（原始值）
            float PixelX,   // 该字在行内真实像素起点（= _wordLayouts[i].X - minX）
            float VelIn,    // 进入切线速度 px/ms
            float VelOut    // 离开切线速度 px/ms
        );

        private sealed record RowCurve(TimeSpan Origin, List<CurvePoint> Points);
        private readonly List<RowCurve> _rowCurves = [];

        // 速度平滑窗口半径：前后各取 N 个字参与加权平均
        private const int VelocitySmoothRadius = 2;

        // Hermite 切线速度上限倍率（相对于该字自然速度），防止过冲
        private const double MaxTangentRatio = 1.6;

        // ── 独立时钟 ─────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private DateTimeOffset _lastTickAt;
        private TimeSpan _currentTime = TimeSpan.Zero;
        private TimeSpan _lastExternalTime = TimeSpan.Zero;
        private const double SyncThresholdMs = 150.0;

        // ── 平滑追赶（消除 seek / 行切换跳变） ────────────────────────
        private readonly Dictionary<int, float> _smoothedRevealX = [];
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

        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

        // ── 构造 ─────────────────────────────────────────────────────
        public AnimatedLyricsLineControl()
        {
            DefaultStyleKey = typeof(AnimatedLyricsLineControl);
            // 读依赖属性当前值（默认 false = 浅色），不硬编码
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

            // 模板应用时强制同步颜色：
            // x:Bind 初始值 == 依赖属性默认值时会跳过 SetValue，这里兜底
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

            if (_measuredHeight > 0f && Math.Abs(_cachedWidth - w) < 1f)
                return new Size(availableSize.Width, Math.Max(_measuredHeight, fallback));

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                EnsureLayout(device, w);
                if (_measuredHeight > 0f)
                    return new Size(availableSize.Width, Math.Max(_measuredHeight, fallback));
            }
            catch { }

            return new Size(availableSize.Width, fallback);
        }

        /// <summary>不依赖 Win2D 的保底高度，防止控件塌缩为 0。</summary>
        private float CalcFallbackHeight()
        {
            float lyricLineH = (float)LyricsFontSize * 1.4f;
            float transLineH = string.IsNullOrEmpty(TranslateText)
                ? 0f
                : (float)TranslateFontSize * 1.4f + 6f;
            return RenderPadding * 2f + PaddingV * 2f + lyricLineH + transLineH;
        }

        // ── 设备重建 ─────────────────────────────────────────────────
        private void OnCreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
            => InvalidateLayoutCache();

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
            => InvalidateLayoutCache();

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
                c._smoothedRevealX.Clear();
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
                c._smoothedRevealX.Clear();
                c._canvas?.Invalidate();
            }
        }

        // ── OffsetMs ─────────────────────────────────────────────────
        public static readonly DependencyProperty OffsetMsProperty =
            DependencyProperty.Register(nameof(OffsetMs),
                typeof(double), typeof(AnimatedLyricsLineControl),
                new PropertyMetadata(-50.0, OnOffsetMsChanged));

        /// <summary>
        /// 全局动画偏移（毫秒）。
        /// 正值：动画整体延后（视觉上歌词"慢半拍"）。
        /// 负值：动画整体提前（视觉上歌词"快半拍"）。
        /// 仅影响渲染时的时间查询，不修改任何时间轴数据。
        /// </summary>
        public double OffsetMs
        {
            get => (double)GetValue(OffsetMsProperty);
            set => SetValue(OffsetMsProperty, value);
        }

        private static void OnOffsetMsChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            // 偏移改变只需重绘，不需要重建布局或曲线
            if (d is not AnimatedLyricsLineControl c) return;
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

        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not AnimatedLyricsLineControl c) return;
            c.InvalidateLayoutCache();
            c.InvalidateMeasure();
        }

        // ══════════════════════════════════════════════════════════════
        // 布局 + 曲线缓存
        // ══════════════════════════════════════════════════════════════

        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            _measuredHeight = 0f;
            _smoothedRevealX.Clear();
            _rowCurves.Clear();
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
            _rowCurves.Clear();
            _smoothedRevealX.Clear();

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
                string wt = words[i].Word;
                int len = Math.Max(wt.Length, 1);
                var regions = layout.GetCharacterRegions(charOffset, len);

                if (regions.Length > 0)
                {
                    Rect first = regions[0].LayoutBounds;
                    Rect last = regions[^1].LayoutBounds;
                    float fw = (float)(last.Right - first.Left);
                    float h = (float)first.Height;
                    _wordLayouts.Add(fw > 0 && h > 0
                        ? new WordLayout(wt,
                            (float)first.Left + alignOffsetX + RenderPadding,
                            (float)first.Top, fw, h)
                        : new WordLayout(wt, RenderPadding, 0, 0, 0));
                }
                else
                {
                    _wordLayouts.Add(new WordLayout(wt, RenderPadding, 0, 0, 0));
                }

                charOffset += wt.Length;
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

            float totalHeight = RenderPadding + PaddingV + lyricsHeight + translateHeight + PaddingV + RenderPadding;
            _measuredHeight = Math.Max(totalHeight, CalcFallbackHeight());

            // 只在 draw 回调路径（creator == _canvas）写 Height，避免 layout pass 重入
            if (_canvas is not null && creator == _canvas)
            {
                _canvas.Height = _measuredHeight;
                Height = _measuredHeight;
            }

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;

            BuildRowCurves(words);
        }

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算
        // ══════════════════════════════════════════════════════════════

        private void BuildRowCurves(IList<LyricWord> words)
        {
            _rowCurves.Clear();
            int count = Math.Min(words.Count, _wordLayouts.Count);
            if (count == 0) return;

            int rowStart = 0;
            while (rowStart < count)
            {
                while (rowStart < count && _wordLayouts[rowStart].FullWidth <= 0)
                    rowStart++;
                if (rowStart >= count) break;

                var firstWl = _wordLayouts[rowStart];
                float rowY = firstWl.Y, rowH = firstWl.Height;
                float minX = firstWl.X, maxX = firstWl.X + firstWl.FullWidth;
                int rowEnd = rowStart + 1;

                while (rowEnd < count)
                {
                    var wl = _wordLayouts[rowEnd];
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

                _rowCurves.Add(fv <= lv
                    ? BuildSingleRowCurve(words, fv, lv, minX, maxX)
                    : new RowCurve(TimeSpan.Zero, []));

                rowStart = rowEnd;
            }
        }

        private RowCurve BuildSingleRowCurve(
            IList<LyricWord> words,
            int firstValid, int lastValid,
            float minX, float maxX)
        {
            // ── 收集有效字 ────────────────────────────────────────────
            var indices = new List<int>();
            var pxWidths = new List<float>();
            var durMs = new List<double>();

            for (int i = firstValid; i <= lastValid; i++)
            {
                if (_wordLayouts[i].FullWidth <= 0) continue;
                indices.Add(i);
                pxWidths.Add(_wordLayouts[i].FullWidth);
                // Duration=0 给最小值 1ms，防止除零
                durMs.Add(Math.Max(words[i].Duration.TotalMilliseconds, 1.0));
            }

            int n = indices.Count;
            if (n == 0) return new RowCurve(words[firstValid].StartTime, []);

            // ── 自然速度 px/ms ─────────────────────────────────────────
            var natVel = new double[n];
            for (int k = 0; k < n; k++)
                natVel[k] = pxWidths[k] / durMs[k];

            // ── 加权移动平均 → 平滑速度（用作 Hermite 切线） ────────────
            //
            // 权重 = 三角形窗口（距离越近权重越高）× 字像素宽（宽字视觉影响更大）。
            // 平滑速度代表「周围字的感知均值速度」，用作切线使交接处连续过渡。
            var smoothVel = new double[n];
            for (int k = 0; k < n; k++)
            {
                double sumW = 0, sumV = 0;
                for (int j = k - VelocitySmoothRadius; j <= k + VelocitySmoothRadius; j++)
                {
                    if (j < 0 || j >= n) continue;
                    double triW = 1.0 - (double)Math.Abs(j - k) / (VelocitySmoothRadius + 1);
                    double w = triW * pxWidths[j];
                    sumW += w;
                    sumV += natVel[j] * w;
                }
                smoothVel[k] = sumW > 0 ? sumV / sumW : natVel[k];
            }

            // ── 限制切线速度，防止 Hermite 过冲 ───────────────────────
            //
            // 切线速度上限 = 自然速度 × MaxTangentRatio。
            // 若平滑速度超限，退化为自然速度（精确）而非过冲。
            // 同时保证切线非负（扫光不后退）。
            var clampedVel = new double[n];
            for (int k = 0; k < n; k++)
                clampedVel[k] = Math.Clamp(smoothVel[k], 0.0, natVel[k] * MaxTangentRatio);

            // ── 构建控制点 ────────────────────────────────────────────
            //
            // 精确约束严格执行：
            //   TimeMs = 原始 StartTime 偏移（不调整）
            //   PixelX = 真实布局 X（不移动）
            //
            // 切线：VelIn = VelOut = clampedVel（进出速度相等，保证 C1 连续）
            //
            // 末尾哨兵：
            //   TimeMs = 最后一字 EndTime
            //   PixelX = 行总宽
            //   切线   = 最后一字自然速度（不平滑，让最后一字精确收尾）
            var lineOrigin = words[firstValid].StartTime;
            var points = new List<CurvePoint>(n + 1);

            for (int k = 0; k < n; k++)
            {
                int wi = indices[k];
                double tMs = (words[wi].StartTime - lineOrigin).TotalMilliseconds;
                float pixelX = _wordLayouts[wi].X - minX;
                float vel = (float)clampedVel[k];
                points.Add(new CurvePoint(tMs, pixelX, vel, vel));
            }

            // 末尾哨兵
            {
                int lastIdx = indices[n - 1];
                double endMs = (words[lastIdx].StartTime + words[lastIdx].Duration - lineOrigin)
                               .TotalMilliseconds;
                float endVel = (float)natVel[n - 1];
                points.Add(new CurvePoint(endMs, maxX - minX, endVel, endVel));
            }

            return new RowCurve(lineOrigin, points);
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

            if (_measuredHeight > 0f)
            {
                if (Math.Abs(sender.Height - _measuredHeight) > 1f)
                    sender.Height = _measuredHeight;
                if (Math.Abs(Height - _measuredHeight) > 1f)
                    Height = _measuredHeight;
            }

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

            using var lyricsFmt = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = (float)LyricsFontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            int count = Math.Min(words.Count, _wordLayouts.Count);
            if (count == 0) return;

            // ── 暗色底层 ──────────────────────────────────────────────
            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                ds.DrawText(wl.Text,
                    new Rect(wl.X, wl.Y + drawOffsetY + PaddingV, wl.FullWidth, wl.Height),
                    _dimColor, lyricsFmt);
            }

            if (!_isCurrentLine) goto DrawTranslate;

            // ── 视觉行分组 ────────────────────────────────────────────
            var visualRows = new List<(float MinX, float MaxX, float Y, float H,
                                       int WordStart, int WordEnd)>();
            {
                int rs = 0;
                while (rs < count)
                {
                    while (rs < count && _wordLayouts[rs].FullWidth <= 0) rs++;
                    if (rs >= count) break;

                    var first = _wordLayouts[rs];
                    float ry = first.Y, rh = first.Height;
                    float mnX = first.X, mxX = first.X + first.FullWidth;
                    int re = rs + 1;

                    while (re < count)
                    {
                        var wl = _wordLayouts[re];
                        if (wl.FullWidth <= 0) { re++; continue; }
                        if (Math.Abs(wl.Y - ry) > rh * 0.5f) break;
                        if (wl.X < mnX) mnX = wl.X;
                        if (wl.X + wl.FullWidth > mxX) mxX = wl.X + wl.FullWidth;
                        re++;
                    }

                    visualRows.Add((mnX, mxX, ry, rh, rs, re - 1));
                    rs = re;
                }
            }

            // OffsetMs：正值延后 = 查询时间减去偏移；负值提前 = 查询时间加上偏移的绝对值
            // 统一表达：effectiveTime = _currentTime - OffsetMs（ms）
            var effectiveTime = _currentTime - TimeSpan.FromMilliseconds(OffsetMs);

            int rowCount = visualRows.Count;
            const float kDeltaSec = 0.016f;

            for (int ri = 0; ri < rowCount; ri++)
            {
                var (minX, maxX, rowY, rowH, wordStart, wordEnd) = visualRows[ri];
                if (maxX - minX <= 0) continue;

                int fv = wordStart;
                while (fv <= wordEnd && _wordLayouts[fv].FullWidth <= 0) fv++;
                int lv = wordEnd;
                while (lv >= wordStart && _wordLayouts[lv].FullWidth <= 0) lv--;
                if (fv > lv) continue;

                // ── 目标 revealX（Hermite 样条插值） ──────────────────
                float targetRevealX = ri < _rowCurves.Count && _rowCurves[ri].Points.Count > 0
                    ? CalcRevealXHermite(_rowCurves[ri], effectiveTime, minX, maxX)
                    : CalcRevealXFallback(words, fv, lv, minX, effectiveTime);

                // ── 指数衰减 lerp（消除 seek / 行切换跳变） ────────────
                if (!_smoothedRevealX.TryGetValue(ri, out float smoothed))
                    smoothed = targetRevealX;

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
                            var wl = _wordLayouts[i];
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
                        var gradStops = new CanvasGradientStop[]
                        {
                            new() { Color = _brightColor, Position = 0f },
                            new() { Color = Color.FromArgb(0,
                                        _brightColor.R, _brightColor.G, _brightColor.B),
                                    Position = 1f },
                        };
                        using var gradBrush = new CanvasLinearGradientBrush(ds, gradStops)
                        {
                            StartPoint = new Vector2(revealX, 0f),
                            EndPoint = new Vector2(revealX + feather, 0f),
                        };
                        using (ds.CreateLayer(gradBrush,
                            new Rect(revealX, rowY + drawOffsetY + PaddingV, feather, rowH)))
                        {
                            for (int i = fv; i <= lv; i++)
                            {
                                var wl = _wordLayouts[i];
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
                    new Rect(RenderPadding,
                             drawOffsetY + PaddingV + lyricsHeight + 6f,
                             layoutWidth, 9999f),
                    _translateColor, transFmt);
            }
        }

        // ══════════════════════════════════════════════════════════════
        // 三次 Hermite 样条插值
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 三次 Hermite 插值。
        ///
        /// 公式：h(t) = (2t³-3t²+1)·p0 + (t³-2t²+t)·m0·dt
        ///            + (-2t³+3t²)·p1 + (t³-t²)·m1·dt
        ///
        /// 其中 t ∈ [0,1] 是段内时间归一化值，m0/m1 是切线速度（px/ms），
        /// dt 是段长（ms），乘积 m·dt 将速度转换为像素域切线幅度。
        ///
        /// t=0 严格输出 p0，t=1 严格输出 p1（精确约束满足）。
        /// </summary>
        private static float HermiteInterp(
            float p0, float p1,
            float m0, float m1,
            double dt, double elapsed)
        {
            if (dt <= 0) return p1;
            float t = Math.Clamp((float)(elapsed / dt), 0f, 1f);
            float t2 = t * t, t3 = t2 * t;

            // 像素域切线幅度 = vel(px/ms) × dt(ms)
            float tm0 = m0 * (float)dt;
            float tm1 = m1 * (float)dt;

            return (2 * t3 - 3 * t2 + 1) * p0
                 + (t3 - 2 * t2 + t) * tm0
                 + (-2 * t3 + 3 * t2) * p1
                 + (t3 - t2) * tm1;
        }

        /// <summary>
        /// 在 Hermite 曲线上查询当前有效时刻对应的 revealX。
        /// 结果 clamp 到 [p0.PixelX, p1.PixelX] 防止极端情况过冲。
        /// </summary>
        private static float CalcRevealXHermite(
            RowCurve curve, TimeSpan effectiveTime, float minX, float maxX)
        {
            var pts = curve.Points;
            if (pts.Count == 0) return minX;

            double elapsedMs = (effectiveTime - curve.Origin).TotalMilliseconds;

            if (elapsedMs <= pts[0].TimeMs) return minX;
            if (elapsedMs >= pts[^1].TimeMs) return maxX;

            // 二分查找所在段
            int lo = 0, hi = pts.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (pts[mid + 1].TimeMs <= elapsedMs) lo = mid + 1;
                else hi = mid;
            }

            var p0 = pts[lo];
            var p1 = pts[lo + 1];
            double dt = p1.TimeMs - p0.TimeMs;
            double segElapsed = elapsedMs - p0.TimeMs;

            float px = HermiteInterp(p0.PixelX, p1.PixelX,
                                     p0.VelOut, p1.VelIn,
                                     dt, segElapsed);

            // 严格 clamp 到本段范围，防止极端切线导致过冲
            px = Math.Clamp(px,
                Math.Min(p0.PixelX, p1.PixelX),
                Math.Max(p0.PixelX, p1.PixelX));

            return minX + px;
        }

        /// <summary>曲线未就绪时的 fallback（正常情况不触发）。</summary>
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
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                var word = words[i];
                var wordEnd = word.StartTime + word.Duration;
                if (effectiveTime >= wordEnd)
                    accX += wl.FullWidth;
                else if (effectiveTime >= word.StartTime)
                {
                    float t = word.Duration > TimeSpan.Zero
                        ? Math.Clamp((float)((effectiveTime - word.StartTime).TotalMilliseconds
                                             / word.Duration.TotalMilliseconds), 0f, 1f)
                        : 1f;
                    float t2 = t * t;
                    accX += wl.FullWidth * (t2 * (3f - 2f * t)); // smoothstep
                    break;
                }
                else break;
            }
            return accX;
        }

        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new System.Text.StringBuilder(words.Count * 6);
            foreach (var w in words) sb.Append(w.Word);
            return sb.ToString();
        }
    }
}