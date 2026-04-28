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

        // ── 独立时钟 ─────────────────────────────────────────────────
        /// <summary>独立计时器，仅当前行激活时运行。</summary>
        private DispatcherTimer? _timer;

        /// <summary>上一次 tick 时的系统时间，用于计算 delta。</summary>
        private DateTimeOffset _lastTickAt;

        /// <summary>控件内部维护的当前播放时间（由独立时钟驱动）。</summary>
        private TimeSpan _currentTime = TimeSpan.Zero;

        /// <summary>
        /// 外部最近一次同步进来的时间，用于偏差检测。
        /// 超过 150 ms 才强制对齐，消除 200 ms 轮询带来的跳帧感。
        /// </summary>
        private TimeSpan _lastExternalTime = TimeSpan.Zero;

        private const double SyncThresholdMs = 150.0;

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
        private static float EaseInOut(float t)=> t * t * (3f - 2f * t); // smoothstep：头尾各有缓入缓出

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

            // 根据当前状态决定是否需要启动时钟
            UpdateTimerState();
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

        // ══════════════════════════════════════════════════════════════
        // 独立时钟管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 根据 _isCurrentLine / _isPlaying 决定时钟应处于何种状态：
        ///   - isCurrentLine=true  且 isPlaying=true  → 启动 / 保持运行
        ///   - isCurrentLine=true  且 isPlaying=false → 创建但暂停（Stop）
        ///   - isCurrentLine=false                    → 销毁
        /// </summary>
        private void UpdateTimerState()
        {
            if (!_isCurrentLine)
            {
                DestroyTimer();
                return;
            }

            // 当前行：确保 timer 存在
            if (_timer is null)
                CreateTimer();

            if (_isPlaying)
            {
                if (!_timer!.IsEnabled)
                {
                    _lastTickAt = DateTimeOffset.UtcNow;
                    _timer.Start();
                }
            }
            else
            {
                _timer!.Stop();
            }
        }

        private void CreateTimer()
        {
            _timer = new DispatcherTimer
            {
                // ~60 fps；Win2D 本身限速于 vsync，16 ms 足够
                Interval = TimeSpan.FromMilliseconds(16)
            };
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

        /// <summary>
        /// 每 16 ms 累加真实流逝时间到 _currentTime，然后触发重绘。
        /// </summary>
        private void OnTimerTick(object? sender, object e)
        {
            var now = DateTimeOffset.UtcNow;
            var delta = now - _lastTickAt;
            _lastTickAt = now;

            // 防止 delta 异常（如系统休眠唤醒）
            if (delta > TimeSpan.FromSeconds(1))
                delta = TimeSpan.FromMilliseconds(16);

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

        // ── IsCurrentLine ────────────────────────────────────────────
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
                // 非当前行：重置内部时间，销毁时钟
                c._currentTime = TimeSpan.Zero;
                c._lastExternalTime = TimeSpan.Zero;
            }

            c.UpdateTimerState();
            c.IsCurrentLineChanged?.Invoke(c, c._isCurrentLine);
            c._canvas?.Invalidate();
        }

        // ── IsPlaying ────────────────────────────────────────────────
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

            if (c._isPlaying && c._timer is not null)
            {
                // 恢复时钟基准，避免暂停期间时间差被累加进去
                c._lastTickAt = DateTimeOffset.UtcNow;
            }

            c.UpdateTimerState();
        }

        // ── CurrentPlayingTime（外部同步，仅做偏差校正）──────────────
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

            if (!c._isCurrentLine)
                return;

            // 若内部时钟尚未运行（如刚切换到当前行），直接采用外部时间
            if (c._timer is null || !c._timer.IsEnabled)
            {
                c._currentTime = externalTime;
                c._canvas?.Invalidate();
                return;
            }

            // 偏差超过阈值才强制同步，消除 200 ms 轮询的跳帧感
            double diffMs = Math.Abs(
                (externalTime - c._currentTime).TotalMilliseconds);

            if (diffMs > SyncThresholdMs)
            {
                c._currentTime = externalTime;
                // 重置 delta 基准，避免下一个 tick 把跳变前的时间差也累加进去
                c._lastTickAt = DateTimeOffset.UtcNow;
                c._canvas?.Invalidate();
            }
            // 偏差在 150 ms 以内：忽略外部时间，由独立时钟自行推进
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
                            (float)first.Top,
                            fw, h)
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

            // ── 1. 先画所有字的暗色底层 ──────────────────────────────────
            for (int i = 0; i < count; i++)
            {
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;
                ds.DrawText(wl.Text,
                    new Rect(wl.X, wl.Y + drawOffsetY + PaddingV, wl.FullWidth, wl.Height),
                    _dimColor, lyricsFmt);
            }

            if (!_isCurrentLine) goto DrawTranslate;

            // ── 2. 按视觉行分组（Y 值相近的归为同一行） ──────────────────
            //    WordLayout.Y 是相对于 CanvasTextLayout 的顶部，同一行的字 Y 值相同。
            //    用 Y 值是否在上一行 Y + Height 范围内来判断是否同行。
            var visualRows = new List<(float MinX, float MaxX, float Y, float H,
                                        int WordStart, int WordEnd)>();
            {
                int rowStart = 0;
                while (rowStart < count)
                {
                    // 跳过无效字
                    while (rowStart < count && _wordLayouts[rowStart].FullWidth <= 0)
                        rowStart++;
                    if (rowStart >= count) break;

                    var first = _wordLayouts[rowStart];
                    float rowY = first.Y;
                    float rowH = first.Height;
                    float minX = first.X;
                    float maxX = first.X + first.FullWidth;
                    int rowEnd = rowStart + 1;

                    // 同一视觉行：Y 差值小于行高的一半
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

            // ── 3. 整行时间轴：第一个字 StartTime → 最后一个字 StartTime+Duration ──
            var lineStart = words[0].StartTime;
            var lineEnd = words[count - 1].StartTime + words[count - 1].Duration;
            double lineDurationMs = (lineEnd - lineStart).TotalMilliseconds;

            // 当前时间在整行中的全局线性进度（0→1 对应行首→行尾）
            float globalT;
            {
                var elapsed = _currentTime - lineStart;
                if (elapsed <= TimeSpan.Zero)
                    globalT = 0f;
                else if (lineDurationMs <= 0 || _currentTime >= lineEnd)
                    globalT = 1f;
                else
                    globalT = Math.Clamp(
                        (float)(elapsed.TotalMilliseconds / lineDurationMs), 0f, 1f);
            }

            // ── 4. 将全局进度映射为每个视觉行的局部进度 ──────────────────
            //    把整行时间轴均匀分配给各视觉行（按行数等分）；
            //    也可以按各行所含字的时间范围精确分配，见注释。
            int rowCount = visualRows.Count;

            for (int ri = 0; ri < rowCount; ri++)
            {
                var (minX, maxX, rowY, rowH, wordStart, wordEnd) = visualRows[ri];
                float rowWidth = maxX - minX;
                if (rowWidth <= 0) continue;

                // ── 按本行首尾字的真实时间区间算局部 t ──────────────────
                //    找本行第一个和最后一个有效字
                int firstValid = wordStart;
                while (firstValid <= wordEnd && _wordLayouts[firstValid].FullWidth <= 0)
                    firstValid++;
                int lastValid = wordEnd;
                while (lastValid >= wordStart && _wordLayouts[lastValid].FullWidth <= 0)
                    lastValid--;

                if (firstValid > lastValid) continue;

                var rowTimeStart = words[firstValid].StartTime;
                var rowTimeEnd = words[lastValid].StartTime + words[lastValid].Duration;
                double rowDurMs = (rowTimeEnd - rowTimeStart).TotalMilliseconds;

                //float rowT;
                //var rowElapsed = _currentTime - rowTimeStart;
                //if (rowElapsed <= TimeSpan.Zero)
                //    rowT = 0f;
                //else if (rowDurMs <= 0 || _currentTime >= rowTimeEnd)
                //    rowT = 1f;
                //else
                //    rowT = Math.Clamp(
                //        (float)(rowElapsed.TotalMilliseconds / rowDurMs), 0f, 1f);

                float revealX = CalcRevealX(words, firstValid, lastValid, minX);
                float highlightWidth = revealX - minX;
                float rowT = (maxX - minX) > 0f ? (revealX - minX) / (maxX - minX) : 0f;

                // ── 亮色高亮（整行 clip） ─────────────────────────────────
                if (highlightWidth > 0f)
                {
                    using (ds.CreateLayer(1f,
                        new Rect(minX, rowY + drawOffsetY + PaddingV,
                                 highlightWidth, rowH)))
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

                // ── 羽化边缘 ─────────────────────────────────────────────
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

        /// <summary>
        /// 根据当前时间精确计算本视觉行的扫光 X 坐标。
        /// 每个字按自身 StartTime+Duration 占据对应像素宽度，速度与时长成正比。
        /// </summary>
        private float CalcRevealX(
            IList<LyricWord> words,
            int firstValid, int lastValid,
            float minX)
        {
            // 还没开始
            if (_currentTime <= words[firstValid].StartTime)
                return minX;

            // 已经播完
            if (_currentTime >= words[lastValid].StartTime + words[lastValid].Duration)
            {
                float totalW = 0f;
                for (int i = firstValid; i <= lastValid; i++)
                    if (_wordLayouts[i].FullWidth > 0)
                        totalW += _wordLayouts[i].FullWidth;
                return minX + totalW;
            }

            float accX = minX;
            for (int i = firstValid; i <= lastValid; i++)
            {
                var wl = _wordLayouts[i];
                if (wl.FullWidth <= 0) continue;

                var word = words[i];
                var wordEnd = word.StartTime + word.Duration;

                if (_currentTime >= wordEnd)
                {
                    // 这个字已经完全扫过
                    accX += wl.FullWidth;
                }
                else if (_currentTime >= word.StartTime)
                {
                    // 当前时间就落在这个字内部
                    float t = word.Duration > TimeSpan.Zero
                        ? Math.Clamp(
                            (float)((_currentTime - word.StartTime).TotalMilliseconds
                                    / word.Duration.TotalMilliseconds),
                            0f, 1f)
                        : 1f;
                    accX += wl.FullWidth * t;
                    break; // 后面的字还没开始，不用继续
                }
                // else: 这个字还没开始，也 break（后面的也没开始）
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