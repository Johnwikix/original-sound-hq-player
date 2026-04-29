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

        // ── 速度曲线预计算 ────────────────────────────────────────────
        //
        // 每个视觉行对应 _rowCurves 里的一个 List<WordCurvePoint>。
        // 每个控制点记录：
        //   TimeMs  = 该字扫光起点相对于本行首字 StartTime 的毫秒偏移
        //   PixelX  = 扫光到达该控制点时已走过的累计像素（相对于行 minX 的偏移）
        // 末尾追加一个哨兵点 { 最后一字结束时间, 行总宽 }。
        //
        // CalcRevealXFromCurve 在此表上做分段线性插值 + 段内 smoothstep。
        private sealed record WordCurvePoint(double TimeMs, float PixelX);
        private readonly List<List<WordCurvePoint>> _rowCurves = [];

        // 速度平滑窗口半径：前后各取 N 个字参与加权平均
        private const int VelocitySmoothRadius = 2;

        // ── 独立时钟 ─────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private DateTimeOffset _lastTickAt;
        private TimeSpan _currentTime = TimeSpan.Zero;
        private TimeSpan _lastExternalTime = TimeSpan.Zero;
        private const double SyncThresholdMs = 150.0;

        // ── 平滑追赶（消除行切换/seek 时的跳变） ──────────────────────
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

        /// <summary>smoothstep：头尾缓入缓出，用于段内进度缓动。</summary>
        private static float EaseInOut(float t) => t * t * (3f - 2f * t);

        private CanvasHorizontalAlignment _cachedAlignment = CanvasHorizontalAlignment.Left;

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

            UpdateTimerState();
        }

        // ── MeasureOverride ──────────────────────────────────────────
        protected override Size MeasureOverride(Size availableSize)
        {
            float w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 400f
                : (float)availableSize.Width;

            float fallbackHeight = CalcFallbackHeight();

            if (_measuredHeight > 0f && Math.Abs(_cachedWidth - w) < 1f)
                return new Size(availableSize.Width, Math.Max(_measuredHeight, fallbackHeight));

            try
            {
                var device = CanvasDevice.GetSharedDevice();
                EnsureLayout(device, w);
                if (_measuredHeight > 0f)
                    return new Size(availableSize.Width, Math.Max(_measuredHeight, fallbackHeight));
            }
            catch { }

            return new Size(availableSize.Width, fallbackHeight);
        }

        /// <summary>
        /// 不依赖 Win2D 的保底高度，防止 EnsureLayout 失败时控件塌缩为 0。
        /// </summary>
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
        // 独立时钟管理
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

        // ── 布局 + 速度曲线缓存 ───────────────────────────────────────
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
            float fallback = CalcFallbackHeight();
            _measuredHeight = Math.Max(totalHeight, fallback);

            // 只在 draw 回调路径（creator == _canvas）才写 Height，避免 layout pass 重入
            if (_canvas is not null && creator == _canvas)
            {
                _canvas.Height = _measuredHeight;
                Height = _measuredHeight;
            }

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;

            // 布局完成，立即预计算速度曲线
            BuildRowCurves(words);
        }

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 对每个视觉行预计算「时间→累计像素」映射曲线。
        ///
        /// 问题根源：
        ///   原始速度 = FullWidth / Duration，单位 px/ms。
        ///   中文每字宽度相近，但英语单词宽度差异悬殊（"I" vs "World"），
        ///   加上歌词标注时 Duration 不一定与字符宽度成正比，
        ///   导致相邻词像素速度可相差数倍，切换瞬间产生明显速度突变。
        ///
        /// 解决思路：
        ///   1. 计算每个字的原始速度 px/ms。
        ///   2. 对原始速度做加权移动平均（窗口半径 = VelocitySmoothRadius），
        ///      权重 = 三角形窗口 × 字像素宽（宽字对视觉速度感知影响更大）。
        ///   3. 用平滑后的速度 × 原始 Duration 重新推算每个字的像素分配量，
        ///      再等比归一化确保总宽不变，时间轴（StartTime）完全不动。
        ///   4. 将结果存为控制点列表，CalcRevealXFromCurve 做分段线性插值 + smoothstep。
        /// </summary>
        private void BuildRowCurves(IList<LyricWord> words)
        {
            _rowCurves.Clear();
            int count = Math.Min(words.Count, _wordLayouts.Count);
            if (count == 0) return;

            // ── 分组为视觉行（与 DrawContent 保持完全相同的分组逻辑）──
            int rowStart = 0;
            while (rowStart < count)
            {
                while (rowStart < count && _wordLayouts[rowStart].FullWidth <= 0)
                    rowStart++;
                if (rowStart >= count) break;

                var first = _wordLayouts[rowStart];
                float rowY = first.Y;
                float rowH = first.Height;
                float minX = first.X;
                float maxX = first.X + first.FullWidth;
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

                if (fv <= lv)
                    _rowCurves.Add(BuildSingleRowCurve(words, fv, lv, minX, maxX));
                else
                    _rowCurves.Add([]);

                rowStart = rowEnd;
            }
        }

        private List<WordCurvePoint> BuildSingleRowCurve(
            IList<LyricWord> words,
            int firstValid, int lastValid,
            float minX, float maxX)
        {
            // ── 收集本行有效字 ────────────────────────────────────────
            var indices = new List<int>();
            var rawPxWidths = new List<float>();
            var rawDurMs = new List<double>();

            for (int i = firstValid; i <= lastValid; i++)
            {
                if (_wordLayouts[i].FullWidth <= 0) continue;
                indices.Add(i);
                rawPxWidths.Add(_wordLayouts[i].FullWidth);
                // Duration=0 的字（标点/数据缺失）至少给 1ms，防止除零
                rawDurMs.Add(Math.Max(words[i].Duration.TotalMilliseconds, 1.0));
            }

            int n = indices.Count;
            if (n == 0) return [];

            // ── 原始速度 px/ms ────────────────────────────────────────
            var rawVel = new float[n];
            for (int k = 0; k < n; k++)
                rawVel[k] = rawPxWidths[k] / (float)rawDurMs[k];

            // ── 加权移动平均平滑速度 ──────────────────────────────────
            //
            // 权重 = 三角形窗口（中心最高，两侧线性衰减）× 字像素宽。
            // 乘以像素宽是因为：宽字在视线上停留更久，对速度感知权重更大，
            // 平滑时理应向宽字的速度靠拢。
            var smoothVel = new float[n];
            for (int k = 0; k < n; k++)
            {
                float sumW = 0f, sumV = 0f;
                for (int j = k - VelocitySmoothRadius; j <= k + VelocitySmoothRadius; j++)
                {
                    if (j < 0 || j >= n) continue;
                    float triW = 1f - (float)Math.Abs(j - k) / (VelocitySmoothRadius + 1);
                    float impW = rawPxWidths[j]; // 像素宽重要性权重
                    float w = triW * impW;
                    sumW += w;
                    sumV += rawVel[j] * w;
                }
                smoothVel[k] = sumW > 0f ? sumV / sumW : rawVel[k];
            }

            // ── 重新分配像素，归一化保持总宽不变 ─────────────────────
            //
            // allocPx[k] = smoothVel[k] × Duration[k]
            // 之后等比缩放，使 sum(allocPx) == 行总宽。
            // 时间轴（StartTime）完全不变，只有像素分配量被调整。
            float totalRowWidth = maxX - minX;
            var allocPx = new float[n];
            float allocSum = 0f;
            for (int k = 0; k < n; k++)
            {
                allocPx[k] = smoothVel[k] * (float)rawDurMs[k];
                allocSum += allocPx[k];
            }

            if (allocSum > 0f)
            {
                float scale = totalRowWidth / allocSum;
                for (int k = 0; k < n; k++) allocPx[k] *= scale;
            }
            else
            {
                float each = totalRowWidth / n;
                for (int k = 0; k < n; k++) allocPx[k] = each;
            }

            // ── 构建控制点：(StartTimeMs相对行首, 累计像素偏移) ────────
            var lineOrigin = words[firstValid].StartTime;
            var curve = new List<WordCurvePoint>(n + 1);
            float accPx = 0f;

            for (int k = 0; k < n; k++)
            {
                int wi = indices[k];
                double tMs = (words[wi].StartTime - lineOrigin).TotalMilliseconds;
                curve.Add(new WordCurvePoint(tMs, accPx));
                accPx += allocPx[k];
            }

            // 末尾哨兵：最后一字结束 → 行总宽
            {
                int lastIdx = indices[n - 1];
                double endMs = (words[lastIdx].StartTime + words[lastIdx].Duration - lineOrigin).TotalMilliseconds;
                curve.Add(new WordCurvePoint(endMs, totalRowWidth));
            }

            return curve;
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
            {
                DrawContent(clDs, words, w);
            }

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

            // ── 1. 暗色底层 ───────────────────────────────────────────
            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                ds.DrawText(wl.Text,
                    new Rect(wl.X, wl.Y + drawOffsetY + PaddingV, wl.FullWidth, wl.Height),
                    _dimColor, lyricsFmt);
            }

            if (!_isCurrentLine) goto DrawTranslate;

            // ── 2. 视觉行分组 ─────────────────────────────────────────
            var visualRows = new List<(float MinX, float MaxX, float Y, float H,
                                        int WordStart, int WordEnd)>();
            {
                int rowStart = 0;
                while (rowStart < count)
                {
                    while (rowStart < count && _wordLayouts[rowStart].FullWidth <= 0)
                        rowStart++;
                    if (rowStart >= count) break;

                    var first = _wordLayouts[rowStart];
                    float rowY = first.Y;
                    float rowH = first.Height;
                    float minX = first.X;
                    float maxX = first.X + first.FullWidth;
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

                    visualRows.Add((minX, maxX, rowY, rowH, rowStart, rowEnd - 1));
                    rowStart = rowEnd;
                }
            }

            int rowCount = visualRows.Count;
            const float kDeltaSec = 0.016f;

            for (int ri = 0; ri < rowCount; ri++)
            {
                var (minX, maxX, rowY, rowH, wordStart, wordEnd) = visualRows[ri];
                float rowWidth = maxX - minX;
                if (rowWidth <= 0) continue;

                int firstValid = wordStart;
                while (firstValid <= wordEnd && _wordLayouts[firstValid].FullWidth <= 0)
                    firstValid++;
                int lastValid = wordEnd;
                while (lastValid >= wordStart && _wordLayouts[lastValid].FullWidth <= 0)
                    lastValid--;
                if (firstValid > lastValid) continue;

                // ── 从预计算曲线查询目标 revealX ─────────────────────
                float targetRevealX = ri < _rowCurves.Count && _rowCurves[ri].Count > 0
                    ? CalcRevealXFromCurve(_rowCurves[ri], words[firstValid].StartTime, minX, maxX)
                    : CalcRevealXFallback(words, firstValid, lastValid, minX);

                // ── 指数衰减 lerp 平滑追赶（消除 seek / 行切换跳变）──
                if (!_smoothedRevealX.TryGetValue(ri, out float smoothed))
                    smoothed = targetRevealX;

                float diff = targetRevealX - smoothed;
                if (Math.Abs(diff) > 0.5f)
                {
                    float lerpFactor = 1f - MathF.Exp(-SmoothLerpSpeed * kDeltaSec);
                    float lerpStep = diff * lerpFactor;
                    float maxStep = SmoothMaxPixelsPerSec * kDeltaSec;
                    if (Math.Abs(lerpStep) > maxStep) lerpStep = Math.Sign(lerpStep) * maxStep;
                    smoothed += lerpStep;
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
                        for (int i = firstValid; i <= lastValid; i++)
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
                    float featherActual = Math.Min(FeatherWidth, maxX - revealX);
                    if (featherActual > 0.5f)
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
                            EndPoint = new Vector2(revealX + featherActual, 0f),
                        };
                        using (ds.CreateLayer(gradBrush,
                            new Rect(revealX, rowY + drawOffsetY + PaddingV,
                                     featherActual, rowH)))
                        {
                            for (int i = firstValid; i <= lastValid; i++)
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
        // 曲线查询
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 在预计算曲线上做分段线性插值 + 段内 smoothstep，
        /// 返回当前时刻对应的 revealX 像素坐标。
        ///
        /// 两层缓动叠加：
        ///   行级：速度平滑后的控制点（消除词宽/时长比例不一致）
        ///   段内：smoothstep（让每个字起止时的速度感更圆滑）
        /// </summary>
        private float CalcRevealXFromCurve(
            List<WordCurvePoint> curve,
            TimeSpan rowOrigin,
            float minX, float maxX)
        {
            if (curve.Count == 0) return minX;

            double elapsedMs = (_currentTime - rowOrigin).TotalMilliseconds;

            if (elapsedMs <= curve[0].TimeMs) return minX;
            if (elapsedMs >= curve[^1].TimeMs) return maxX;

            // 二分查找所在段
            int lo = 0, hi = curve.Count - 2;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (curve[mid + 1].TimeMs <= elapsedMs) lo = mid + 1;
                else hi = mid;
            }

            var p0 = curve[lo];
            var p1 = curve[lo + 1];
            double segDur = p1.TimeMs - p0.TimeMs;

            float t = segDur > 0
                ? Math.Clamp((float)((elapsedMs - p0.TimeMs) / segDur), 0f, 1f)
                : 1f;

            float px = p0.PixelX + (p1.PixelX - p0.PixelX) * EaseInOut(t);
            return minX + px;
        }

        /// <summary>曲线未就绪时的退路（正常情况下不应触发）。</summary>
        private float CalcRevealXFallback(
            IList<LyricWord> words, int firstValid, int lastValid, float minX)
        {
            if (_currentTime <= words[firstValid].StartTime) return minX;

            float totalW = 0f;
            for (int i = firstValid; i <= lastValid; i++)
                if (_wordLayouts[i].FullWidth > 0) totalW += _wordLayouts[i].FullWidth;

            if (_currentTime >= words[lastValid].StartTime + words[lastValid].Duration)
                return minX + totalW;

            float accX = minX;
            for (int i = firstValid; i <= lastValid; i++)
            {
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                var word = words[i];
                var wordEnd = word.StartTime + word.Duration;
                if (_currentTime >= wordEnd)
                    accX += wl.FullWidth;
                else if (_currentTime >= word.StartTime)
                {
                    float t = word.Duration > TimeSpan.Zero
                        ? Math.Clamp(
                            (float)((_currentTime - word.StartTime).TotalMilliseconds
                                    / word.Duration.TotalMilliseconds), 0f, 1f)
                        : 1f;
                    accX += wl.FullWidth * EaseInOut(t);
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