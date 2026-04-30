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

        // ── 设备未就绪时需要在 CreateResources 后重新 Measure ────────
        private bool _needsRemeasure = false;

        // ══════════════════════════════════════════════════════════════
        // 速度曲线预计算（三次 Hermite 样条）
        // ══════════════════════════════════════════════════════════════

        private sealed record CurvePoint(
            double TimeMs,
            float PixelX,
            float VelIn,
            float VelOut
        );

        private sealed record RowCurve(TimeSpan Origin, List<CurvePoint> Points);
        private readonly List<RowCurve> _rowCurves = [];

        private const int VelocitySmoothRadius = 2;
        private const double MaxTangentRatio = 1.6;

        // ── 独立时钟 ─────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private DateTimeOffset _lastTickAt;
        private TimeSpan _currentTime = TimeSpan.Zero;
        private TimeSpan _lastExternalTime = TimeSpan.Zero;
        private const double SyncThresholdMs = 150.0;

        // ── 平滑追赶 ─────────────────────────────────────────────────
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

            // 缓存命中判断：不依赖 _measuredHeight > 0，而是检查所有缓存键
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
                // 设备未就绪，等 CreateResources 触发后重新测量
                // 返回已知高度（若有）或 fallback，不返回 0
                _needsRemeasure = true;
                return new Size(availableSize.Width,
                    _measuredHeight > 0f ? _measuredHeight : fallback);
            }

            return new Size(availableSize.Width,
                _measuredHeight > 0f ? _measuredHeight : fallback);
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
        {
            InvalidateLayoutCache();

            // 设备就绪后，若之前 Measure 因设备未就绪而返回了 fallback，重新触发 Measure
            if (_needsRemeasure)
            {
                _needsRemeasure = false;
                InvalidateMeasure();
            }
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 只清字符缓存和曲线缓存，保留 _measuredHeight 作为过渡高度
            // 避免清零后在重新 Measure 完成之前控件高度塌缩为 fallback 造成抖动
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);
            _smoothedRevealX.Clear();
            _rowCurves.Clear();
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
        // 布局缓存失效
        // ══════════════════════════════════════════════════════════════

        private void InvalidateLayoutCache()
        {
            _cachedText = null;
            _cachedWidth = 0f;
            _cachedFontSize = 0f;
            _cachedAlignment = (CanvasHorizontalAlignment)(-1);

            // ❌ 不清零 _measuredHeight
            // 保留上一次已知高度作为过渡值，防止 Measure 重入期间高度塌缩为 0
            // 等 EnsureLayout 重新计算后自然覆盖

            _smoothedRevealX.Clear();
            _rowCurves.Clear();
            _canvas?.Invalidate();
        }

        // ══════════════════════════════════════════════════════════════
        // 布局计算（Measure 路径 + Draw 路径共用，但行为不同）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 核心布局计算。
        /// Measure 路径：由 MeasureOverride 调用，结果通过返回值传递给布局系统。
        /// Draw 路径：由 EnsureLayoutForDraw 调用，不修改任何影响布局的属性。
        /// </summary>
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
            // 只更新 _measuredHeight，绝不在这里修改 Height / _canvas.Height
            // 高度通过 MeasureOverride 返回值传递给布局系统
            _measuredHeight = Math.Max(totalHeight, CalcFallbackHeight());

            _cachedText = fullText;
            _cachedWidth = availableWidth;
            _cachedFontSize = fontSize;
            _cachedAlignment = alignment;

            BuildRowCurves(words);
        }

        /// <summary>
        /// Draw 路径专用的 EnsureLayout 入口。
        /// 宽度容差放宽到 2px 避免浮点抖动反复重建。
        /// 若布局重建后高度发生变化，通过 DispatcherQueue 异步通知布局系统，
        /// 绝不在 Draw 回调的调用栈内修改任何影响布局的属性。
        /// </summary>
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

            // 布局重建后高度变了 → 异步通知布局系统重新 Measure
            // 不能在 Draw 回调内同步调用 InvalidateMeasure，会触发布局重入
            if (Math.Abs(_measuredHeight - oldHeight) > 1f)
            {
                DispatcherQueue.TryEnqueue(InvalidateMeasure);
            }
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
            var indices = new List<int>();
            var pxWidths = new List<float>();
            var durMs = new List<double>();

            for (int i = firstValid; i <= lastValid; i++)
            {
                if (_wordLayouts[i].FullWidth <= 0) continue;
                indices.Add(i);
                pxWidths.Add(_wordLayouts[i].FullWidth);
                durMs.Add(Math.Max(words[i].Duration.TotalMilliseconds, 1.0));
            }

            int n = indices.Count;
            if (n == 0) return new RowCurve(words[firstValid].StartTime, []);

            var natVel = new double[n];
            for (int k = 0; k < n; k++)
                natVel[k] = pxWidths[k] / durMs[k];

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

            var clampedVel = new double[n];
            for (int k = 0; k < n; k++)
                clampedVel[k] = Math.Clamp(smoothVel[k], 0.0, natVel[k] * MaxTangentRatio);

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

            {
                int lastIdx = indices[n - 1];
                double endMs = (words[lastIdx].StartTime + words[lastIdx].Duration - lineOrigin)
                               .TotalMilliseconds;
                float endVel = (float)natVel[n - 1];
                points.Add(new CurvePoint(endMs, maxX - minX, endVel, endVel));
            }

            return new RowCurve(lineOrigin, points);
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

            // Draw 路径：使用专用入口，不在渲染回调内修改任何布局属性
            EnsureLayoutForDraw(ds, w);
            if (_wordLayouts.Count == 0) return;

            // ❌ 以下两行已彻底删除，Draw 回调内绝不修改 Height
            // sender.Height = _measuredHeight;
            // Height = _measuredHeight;

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

                float targetRevealX = ri < _rowCurves.Count && _rowCurves[ri].Points.Count > 0
                    ? CalcRevealXHermite(_rowCurves[ri], effectiveTime, minX, maxX)
                    : CalcRevealXFallback(words, fv, lv, minX, effectiveTime);

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
            RowCurve curve, TimeSpan effectiveTime, float minX, float maxX)
        {
            var pts = curve.Points;
            if (pts.Count == 0) return minX;

            double elapsedMs = (effectiveTime - curve.Origin).TotalMilliseconds;

            if (elapsedMs <= pts[0].TimeMs) return minX;
            if (elapsedMs >= pts[^1].TimeMs) return maxX;

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
                    accX += wl.FullWidth * (t2 * (3f - 2f * t));
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