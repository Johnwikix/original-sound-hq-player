using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public sealed class UnifiedLyricsCanvasControl : Control
    {
        // ─────────────────────────────────────────────────────────────────────
        // 公开事件
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>点击某行歌词时触发，携带该行起始时间戳，供外部跳转播放位置。</summary>
        public event EventHandler<TimeSpan>? LyricLineClicked;

        /// <summary>
        /// 当前播放行的 Y 偏移（相对 CanvasControl 顶部，单位 px）发生变化时触发。
        /// 外部 ScrollView behavior 监听此值驱动自动滚动。
        /// </summary>
        public event EventHandler<double>? CurrentLineOffsetYChanged;

        // ─────────────────────────────────────────────────────────────────────
        // PART
        // ─────────────────────────────────────────────────────────────────────
        private CanvasControl? _canvas;

        // ─────────────────────────────────────────────────────────────────────
        // 布局缓存结构体
        // ─────────────────────────────────────────────────────────────────────

        private struct WordLayout
        {
            public string Text;
            public float X, Y, FullWidth, Height;
        }

        /// <summary>每首歌词行的布局信息（画布坐标系）。</summary>
        private struct LineLayout
        {
            public int WordStart;           // 在 _wordLayouts 中的起始全局索引
            public int WordCount;
            public float OffsetY;           // 本行在画布中的起始 Y（含上方所有行高 + 行间距）
            public float Height;            // 本行总高（PaddingV + 歌词 + 翻译 + PaddingV）
            public float TranslateOffsetY;  // 翻译行相对于 OffsetY 的 Y 偏移
            public bool HasTranslate;
        }

        /// <summary>由自动换行产生的单视觉行（行内可能有多条）。</summary>
        private struct VisualRow
        {
            public float MinX, MaxX, Y, H;
            public int WordStart, WordEnd;  // 全局 _wordLayouts 索引
        }

        private struct LineVisualRowRange
        {
            public int Start, Count;        // 在 _visualRows 中的范围
        }

        private WordLayout[] _wordLayouts = [];
        private int _totalWordCount = 0;
        private LineLayout[] _lineLayouts = [];
        private int _lineLayoutCount = 0;
        private VisualRow[] _visualRows = [];
        private int _visualRowCount = 0;
        private LineVisualRowRange[] _lineVisualRowRanges = [];

        private string? _cachedLayoutKey = null;
        private float _cachedLayoutWidth = 0f;
        private float _totalCanvasHeight = 0f;

        // ─────────────────────────────────────────────────────────────────────
        // 速度曲线（Hermite Catmull-Rom）
        // ─────────────────────────────────────────────────────────────────────

        private struct CurvePoint
        {
            public double TimeMs;
            public float PixelX;
            public float VelIn, VelOut;
        }

        private struct RowCurve
        {
            public TimeSpan Origin;
            public CurvePoint[]? Points;
            public int Count;
        }

        // _rowCurves 的索引与"所有行的视觉行"全局序号对齐
        private RowCurve[] _rowCurves = [];
        private int _rowCurveCount = 0;

        // ─────────────────────────────────────────────────────────────────────
        // 平滑追赶（每个视觉行一个槽）
        // ─────────────────────────────────────────────────────────────────────
        private float[] _smoothedRevealX = [];
        private const float SmoothLerpSpeed = 18f;
        private const float SmoothMaxPixelsPerSec = 2000f;

        // ─────────────────────────────────────────────────────────────────────
        // 独立时钟
        // ─────────────────────────────────────────────────────────────────────
        private DispatcherTimer? _timer;
        private DateTimeOffset _lastTickAt;
        private TimeSpan _currentTime = TimeSpan.Zero;
        private TimeSpan _lastExternalTime = TimeSpan.Zero;
        private const double SyncThresholdMs = 150.0;

        // ─────────────────────────────────────────────────────────────────────
        // 渲染状态
        // ─────────────────────────────────────────────────────────────────────
        private int _currentLineIndex = -1;
        private bool _isPlaying = false;
        private double _currentLineOffsetY = 0.0;

        // ─────────────────────────────────────────────────────────────────────
        // 视觉常量
        // ─────────────────────────────────────────────────────────────────────
        private Color _dimColor;
        private Color _brightColor;
        private Color _translateColor;

        private const float FeatherWidth = 22f;
        private const float PaddingV = 12f;
        private const float LineGapV = 8f;
        private const float RenderPaddingH = 10f;
        private const float TranslateGapV = 3f;

        // ─────────────────────────────────────────────────────────────────────
        // CanvasTextFormat 跨帧复用
        // ─────────────────────────────────────────────────────────────────────
        private CanvasTextFormat? _lyricsFmt;
        private CanvasTextFormat? _transFmt;
        private string? _cachedFontFamily;
        private float _cachedLyricsFontSizeForFmt;
        private float _cachedTransFontSizeForFmt;
        private CanvasHorizontalAlignment _cachedTransAlignmentForFmt;

        private readonly CanvasGradientStop[] _gradStops = new CanvasGradientStop[2];
        private bool _gradStopsValid = false;

        // ─────────────────────────────────────────────────────────────────────
        // 构造 / 生命周期
        // ─────────────────────────────────────────────────────────────────────

        public UnifiedLyricsCanvasControl()
        {
            DefaultStyleKey = typeof(UnifiedLyricsCanvasControl);
            UpdateColors(false);
            Unloaded += OnUnloaded;
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            DestroyTimer();
            if (_canvas is not null)
            {
                _canvas.Draw -= OnDraw;
                _canvas.SizeChanged -= OnCanvasSizeChanged;
                _canvas.CreateResources -= OnCreateResources;
                _canvas.Tapped -= OnCanvasTapped;
                _canvas = null;
            }
            DisposeFmtCache();
        }

        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            if (_canvas is not null)
            {
                _canvas.Draw -= OnDraw;
                _canvas.SizeChanged -= OnCanvasSizeChanged;
                _canvas.CreateResources -= OnCreateResources;
                _canvas.Tapped -= OnCanvasTapped;
            }
            _canvas = GetTemplateChild("PART_Canvas") as CanvasControl;
            if (_canvas is not null)
            {
                _canvas.Draw += OnDraw;
                _canvas.SizeChanged += OnCanvasSizeChanged;
                _canvas.CreateResources += OnCreateResources;
                _canvas.Tapped += OnCanvasTapped;
                _canvas.ClearColor = Colors.Transparent;
            }
            UpdateColors(IsDark);
            UpdateTimerState();
        }

        // ─────────────────────────────────────────────────────────────────────
        // MeasureOverride
        // ─────────────────────────────────────────────────────────────────────

        protected override Size MeasureOverride(Size availableSize)
        {
            float w = double.IsInfinity(availableSize.Width) || availableSize.Width <= 0
                ? 400f : (float)availableSize.Width;
            try
            {
                EnsureLayout(CanvasDevice.GetSharedDevice(), w);
            }
            catch { /* device not ready */ }
            return new Size(availableSize.Width, _totalCanvasHeight > 0 ? _totalCanvasHeight : 100);
        }

        // ═════════════════════════════════════════════════════════════════════
        // 依赖属性
        // ═════════════════════════════════════════════════════════════════════

        // ── UILyrics ──────────────────────────────────────────────────────────
        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(nameof(UILyrics),
                typeof(ObservableCollection<LyricLine>), typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    var c = (UnifiedLyricsCanvasControl)d;
                    c.InvalidateLayoutCache();
                    c.InvalidateMeasure();
                }));

        public ObservableCollection<LyricLine>? UILyrics
        {
            get => (ObservableCollection<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

        // ── CurrentPlayingTime ────────────────────────────────────────────────
        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(nameof(CurrentPlayingTime),
                typeof(TimeSpan), typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            var ext = (TimeSpan)e.NewValue;
            c._lastExternalTime = ext;
            if (c._timer is null || !c._timer.IsEnabled)
            {
                c._currentTime = ext;
                c._canvas?.Invalidate();
                return;
            }
            if (Math.Abs((ext - c._currentTime).TotalMilliseconds) > SyncThresholdMs)
            {
                c._currentTime = ext;
                c._lastTickAt = DateTimeOffset.UtcNow;
                c.ResetSmoothedRevealX();
                c._canvas?.Invalidate();
            }
        }

        // ── CurrentLineIndex ──────────────────────────────────────────────────
        public static readonly DependencyProperty CurrentLineIndexProperty =
            DependencyProperty.Register(nameof(CurrentLineIndex),
                typeof(int), typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(-1, OnCurrentLineIndexChanged));

        /// <summary>
        /// 当前高亮行的索引（对应 UILyrics 集合索引）。
        /// 由外部 LyricsControl 的歌词匹配逻辑写入。
        /// </summary>
        public int CurrentLineIndex
        {
            get => (int)GetValue(CurrentLineIndexProperty);
            set => SetValue(CurrentLineIndexProperty, value);
        }

        private static void OnCurrentLineIndexChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c._currentLineIndex = (int)e.NewValue;
            c.ResetSmoothedRevealX();
            c._currentTime = c._lastExternalTime;
            c.UpdateTimerState();
            c.UpdateCurrentLineOffsetY();
            c._canvas?.Invalidate();
        }

        // ── IsPlaying ─────────────────────────────────────────────────────────
        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying),
                typeof(bool), typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        private static void OnIsPlayingChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c._isPlaying = (bool)e.NewValue;
            if (c._isPlaying && c._timer is not null) c._lastTickAt = DateTimeOffset.UtcNow;
            c.UpdateTimerState();
        }

        // ── LyricsFontSize / TranslateFontSize / FontFamilyName / LyricsTextAlignment ──
        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(nameof(LyricsFontSize), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(36.0, OnLayoutPropertyChanged));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(nameof(TranslateFontSize), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(24.0, OnLayoutPropertyChanged));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        public static readonly DependencyProperty FontFamilyNameProperty =
            DependencyProperty.Register(nameof(FontFamilyName), typeof(string),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata("Segoe UI", OnLayoutPropertyChanged));

        public string FontFamilyName
        {
            get => (string)GetValue(FontFamilyNameProperty);
            set => SetValue(FontFamilyNameProperty, value);
        }

        public static readonly DependencyProperty LyricsTextAlignmentProperty =
            DependencyProperty.Register(nameof(LyricsTextAlignment), typeof(CanvasHorizontalAlignment),
                typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(CanvasHorizontalAlignment.Left, OnLayoutPropertyChanged));

        public CanvasHorizontalAlignment LyricsTextAlignment
        {
            get => (CanvasHorizontalAlignment)GetValue(LyricsTextAlignmentProperty);
            set => SetValue(LyricsTextAlignmentProperty, value);
        }

        // ── IsDark ────────────────────────────────────────────────────────────
        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(false, OnIsDarkChanged));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        private static void OnIsDarkChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c.UpdateColors((bool)e.NewValue);
            c._gradStopsValid = false;
            c._canvas?.Invalidate();
        }

        // ── AnimationSmoothness ───────────────────────────────────────────────
        public static readonly DependencyProperty AnimationSmoothnessProperty =
            DependencyProperty.Register(nameof(AnimationSmoothness), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(0.65, OnAnimationSmoothnessChanged));

        public double AnimationSmoothness
        {
            get => (double)GetValue(AnimationSmoothnessProperty);
            set => SetValue(AnimationSmoothnessProperty, Math.Clamp(value, 0.0, 1.0));
        }

        private static void OnAnimationSmoothnessChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c._rowCurveCount = 0;
            c.RebuildAllRowCurves();
            c._canvas?.Invalidate();
        }

        // ── OffsetMs ──────────────────────────────────────────────────────────
        public static readonly DependencyProperty OffsetMsProperty =
            DependencyProperty.Register(nameof(OffsetMs), typeof(double),
                typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(0.0, (d, _) => (d as UnifiedLyricsCanvasControl)?._canvas?.Invalidate()));

        public double OffsetMs
        {
            get => (double)GetValue(OffsetMsProperty);
            set => SetValue(OffsetMsProperty, value);
        }

        // ── CurrentLineOffsetY（只读） ─────────────────────────────────────────
        private static readonly DependencyProperty CurrentLineOffsetYProperty =
            DependencyProperty.Register(nameof(CurrentLineOffsetY), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(0.0));


        //public static readonly DependencyProperty CurrentLineOffsetYProperty =
        //    CurrentLineOffsetYPropertyKey.DependencyProperty;

        /// <summary>
        /// 当前高亮行在 CanvasControl 中的 Y 偏移（px）。
        /// 外部 ScrollView behavior 绑定此值，无需遍历 ListView。
        /// </summary>
        public double CurrentLineOffsetY
        {
            get => (double)GetValue(CurrentLineOffsetYProperty);
            private set => SetValue(CurrentLineOffsetYProperty, value);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c.InvalidateLayoutCache();
            c.InvalidateMeasure();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 时钟
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateTimerState()
        {
            if (_currentLineIndex < 0) { DestroyTimer(); return; }
            if (_timer is null) CreateTimer();
            if (_isPlaying)
            {
                if (!_timer!.IsEnabled) { _lastTickAt = DateTimeOffset.UtcNow; _timer.Start(); }
            }
            else { _timer!.Stop(); }
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

        // ═════════════════════════════════════════════════════════════════════
        // 设备重建 / 尺寸变化
        // ═════════════════════════════════════════════════════════════════════

        private void OnCreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            DisposeFmtCache();
            InvalidateLayoutCache();
            InvalidateMeasure();
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateLayoutCache();
            InvalidateMeasure();
            _canvas?.Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 点击命中测试 → LyricLineClicked 事件
        // ═════════════════════════════════════════════════════════════════════

        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_lineLayoutCount == 0) return;
            float tapY = (float)e.GetPosition(_canvas).Y;
            for (int li = 0; li < _lineLayoutCount; li++)
            {
                ref readonly var ll = ref _lineLayouts[li];
                if (tapY >= ll.OffsetY && tapY < ll.OffsetY + ll.Height)
                {
                    var lyrics = UILyrics;
                    if (lyrics != null && li < lyrics.Count)
                        LyricLineClicked?.Invoke(this, lyrics[li].Time);
                    return;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CurrentLineOffsetY 更新
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateCurrentLineOffsetY()
        {
            double newY = (_currentLineIndex >= 0 && _currentLineIndex < _lineLayoutCount)
                ? _lineLayouts[_currentLineIndex].OffsetY
                : 0.0;

            if (Math.Abs(newY - _currentLineOffsetY) > 0.5)
            {
                _currentLineOffsetY = newY;
                CurrentLineOffsetY = newY;
                CurrentLineOffsetYChanged?.Invoke(this, newY);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 布局计算：EnsureLayout
        // ═════════════════════════════════════════════════════════════════════

        private void EnsureLayout(ICanvasResourceCreator creator, float availableWidth)
        {
            var lyrics = UILyrics;
            if (lyrics is null || lyrics.Count == 0)
            {
                _totalCanvasHeight = 0;
                _lineLayoutCount = 0;
                return;
            }

            string key = BuildLayoutKey(lyrics);
            if (key == _cachedLayoutKey && Math.Abs(availableWidth - _cachedLayoutWidth) < 1f)
                return;

            int lineCount = lyrics.Count;

            // 扩容
            if (_lineLayouts.Length < lineCount) _lineLayouts = new LineLayout[lineCount + 4];
            if (_lineVisualRowRanges.Length < lineCount) _lineVisualRowRanges = new LineVisualRowRange[lineCount + 4];

            int totalWords = 0;
            foreach (var line in lyrics) totalWords += line.Words.Count;
            if (_wordLayouts.Length < totalWords + 8) _wordLayouts = new WordLayout[totalWords + 32];

            float layoutWidth = Math.Max(1f, availableWidth - RenderPaddingH * 2f);
            float fontSize = (float)LyricsFontSize;
            var alignment = LyricsTextAlignment;

            using var lyricsFmtTmp = new CanvasTextFormat
            {
                FontFamily = FontFamilyName,
                FontSize = fontSize,
                FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                WordWrapping = CanvasWordWrapping.WholeWord,
                HorizontalAlignment = CanvasHorizontalAlignment.Left,
            };

            _totalWordCount = 0;
            _lineLayoutCount = 0;
            _visualRowCount = 0;
            _rowCurveCount = 0;

            float cursorY = 0f;

            for (int li = 0; li < lineCount; li++)
            {
                var line = lyrics[li];
                var words = line.Words;
                int wc = words.Count;
                string fullText = BuildFullText(words);

                using var textLayout = new CanvasTextLayout(creator, fullText, lyricsFmtTmp, layoutWidth, 9999f);
                float lineTextWidth = (float)textLayout.LayoutBounds.Width;
                float alignOffsetX = alignment switch
                {
                    CanvasHorizontalAlignment.Center => (layoutWidth - lineTextWidth) / 2f,
                    CanvasHorizontalAlignment.Right => layoutWidth - lineTextWidth,
                    _ => 0f,
                };

                int wordGlobalStart = _totalWordCount;
                int charOffset = 0;

                for (int wi = 0; wi < wc; wi++)
                {
                    string wt = words[wi].Word;
                    int len = Math.Max(wt.Length, 1);
                    var regions = textLayout.GetCharacterRegions(charOffset, len);
                    if (regions.Length > 0)
                    {
                        Rect first = regions[0].LayoutBounds;
                        Rect last = regions[^1].LayoutBounds;
                        float fw = (float)(last.Right - first.Left);
                        float h = (float)first.Height;
                        _wordLayouts[wordGlobalStart + wi] = fw > 0 && h > 0
                            ? new WordLayout
                            {
                                Text = wt,
                                X = (float)first.Left + alignOffsetX + RenderPaddingH,
                                Y = (float)first.Top,
                                FullWidth = fw,
                                Height = h,
                            }
                            : new WordLayout { Text = wt, X = RenderPaddingH };
                    }
                    else
                    {
                        _wordLayouts[wordGlobalStart + wi] = new WordLayout { Text = wt, X = RenderPaddingH };
                    }
                    charOffset += wt.Length;
                }

                float lyricsH = (float)textLayout.LayoutBounds.Height;
                float translateH = 0f;
                if (!string.IsNullOrEmpty(line.TransLateText))
                {
                    using var transFmtTmp = new CanvasTextFormat
                    {
                        FontFamily = FontFamilyName,
                        FontSize = (float)TranslateFontSize,
                        FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                        WordWrapping = CanvasWordWrapping.WholeWord,
                        HorizontalAlignment = CanvasHorizontalAlignment.Left,
                    };
                    using var transLayout = new CanvasTextLayout(creator, line.TransLateText, transFmtTmp, layoutWidth, 9999f);
                    translateH = (float)transLayout.LayoutBounds.Height + TranslateGapV;
                }

                float lineH = PaddingV + lyricsH + translateH + PaddingV;

                int visualRowStart = _visualRowCount;
                BuildVisualRowsForLine(wordGlobalStart, wc);
                int visualRowCount = _visualRowCount - visualRowStart;

                _lineVisualRowRanges[li] = new LineVisualRowRange { Start = visualRowStart, Count = visualRowCount };
                _lineLayouts[li] = new LineLayout
                {
                    WordStart = wordGlobalStart,
                    WordCount = wc,
                    OffsetY = cursorY,
                    Height = lineH,
                    TranslateOffsetY = PaddingV + lyricsH + TranslateGapV,
                    HasTranslate = !string.IsNullOrEmpty(line.TransLateText),
                };

                _totalWordCount += wc;
                _lineLayoutCount++;
                cursorY += lineH + LineGapV;
            }

            _totalCanvasHeight = cursorY;
            _cachedLayoutKey = key;
            _cachedLayoutWidth = availableWidth;

            EnsureSmoothedRevealXCapacity(_visualRowCount);
            RebuildAllRowCurves();
            DisposeFmtCache();
            UpdateCurrentLineOffsetY();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 视觉行预计算（行内自动换行 → 多个 VisualRow）
        // ─────────────────────────────────────────────────────────────────────

        private void BuildVisualRowsForLine(int wordGlobalStart, int wordCount)
        {
            int end = wordGlobalStart + wordCount;
            int rs = wordGlobalStart;
            while (rs < end)
            {
                while (rs < end && _wordLayouts[rs].FullWidth <= 0) rs++;
                if (rs >= end) break;

                ref readonly var first = ref _wordLayouts[rs];
                float ry = first.Y, rh = first.Height;
                float mnX = first.X, mxX = first.X + first.FullWidth;
                int re = rs + 1;

                while (re < end)
                {
                    ref readonly var wl = ref _wordLayouts[re];
                    if (wl.FullWidth <= 0) { re++; continue; }
                    if (Math.Abs(wl.Y - ry) > rh * 0.5f) break;
                    mnX = Math.Min(mnX, wl.X);
                    mxX = Math.Max(mxX, wl.X + wl.FullWidth);
                    re++;
                }

                if (_visualRowCount >= _visualRows.Length)
                {
                    var tmp = new VisualRow[_visualRows.Length * 2 + 4];
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

        // ─────────────────────────────────────────────────────────────────────
        // 速度曲线重建（全局视觉行 → _rowCurves[]）
        // ─────────────────────────────────────────────────────────────────────

        private void RebuildAllRowCurves()
        {
            var lyrics = UILyrics;
            if (lyrics is null || _lineLayoutCount == 0) return;

            double smoothness = Math.Clamp(AnimationSmoothness, 0.0, 1.0);
            _rowCurveCount = 0;

            for (int li = 0; li < _lineLayoutCount; li++)
            {
                ref readonly var ll = ref _lineLayouts[li];
                ref readonly var vrr = ref _lineVisualRowRanges[li];
                var words = lyrics[li].Words;

                for (int ri = 0; ri < vrr.Count; ri++)
                {
                    ref readonly var vr = ref _visualRows[vrr.Start + ri];

                    int fv = vr.WordStart, lv = vr.WordEnd;
                    while (fv <= lv && _wordLayouts[fv].FullWidth <= 0) fv++;
                    while (lv >= fv && _wordLayouts[lv].FullWidth <= 0) lv--;

                    if (_rowCurveCount >= _rowCurves.Length)
                    {
                        var tmp = new RowCurve[_rowCurves.Length * 2 + 4];
                        Array.Copy(_rowCurves, tmp, _rowCurves.Length);
                        _rowCurves = tmp;
                    }

                    _rowCurves[_rowCurveCount] = (fv <= lv)
                        ? BuildSingleRowCurve(words, ll.WordStart, fv, lv, vr.MinX, vr.MaxX, smoothness)
                        : new RowCurve { Count = 0 };

                    _rowCurveCount++;
                }
            }
        }

        /// <param name="words">该行的 LyricWord 列表（行内索引）</param>
        /// <param name="lineWordBase">该行首字在 _wordLayouts 中的全局起始索引</param>
        /// <param name="globalFv">视觉行首字的全局 _wordLayouts 索引</param>
        /// <param name="globalLv">视觉行末字的全局 _wordLayouts 索引</param>
        private RowCurve BuildSingleRowCurve(
            IList<LyricWord> words,
            int lineWordBase,
            int globalFv, int globalLv,
            float minX, float maxX,
            double smoothness)
        {
            int maxN = globalLv - globalFv + 1;
            Span<int> localIndices = maxN <= 64 ? stackalloc int[maxN] : new int[maxN];
            Span<float> pxWidths = maxN <= 64 ? stackalloc float[maxN] : new float[maxN];
            Span<double> durMs = maxN <= 64 ? stackalloc double[maxN] : new double[maxN];

            int n = 0;
            for (int gi = globalFv; gi <= globalLv; gi++)
            {
                ref readonly var wl = ref _wordLayouts[gi];
                if (wl.FullWidth <= 0) continue;
                int local = gi - lineWordBase;
                localIndices[n] = local;
                pxWidths[n] = wl.FullWidth;
                durMs[n] = Math.Max(words[local].Duration.TotalMilliseconds, 1.0);
                n++;
            }

            if (n == 0)
                return new RowCurve { Origin = words[globalFv - lineWordBase].StartTime, Count = 0 };

            // ── 自然速度 ──
            Span<double> natVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++) natVel[k] = pxWidths[k] / durMs[k];

            // ── Catmull-Rom 切线速度 ──
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
                    double tPrev = durMs[k - 1], tNext = durMs[k + 1];
                    catmullVel[k] = (natVel[k - 1] * tNext + natVel[k + 1] * tPrev) / (tPrev + tNext);
                }
            }

            // 防过冲 clamp
            const double maxRatio = 1.8;
            for (int k = 0; k < n; k++)
                catmullVel[k] = Math.Clamp(catmullVel[k], 0.0, natVel[k] * maxRatio);

            // 混合
            Span<double> finalVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++)
                finalVel[k] = natVel[k] + smoothness * (catmullVel[k] - natVel[k]);

            // ── 构建控制点 ──
            const double timeBias = 0.08;
            var lineOrigin = words[localIndices[0]].StartTime;
            var points = new CurvePoint[n + 1];

            for (int k = 0; k < n; k++)
            {
                int local = localIndices[k];
                double origTMs = (words[local].StartTime - lineOrigin).TotalMilliseconds;
                double adjustedTMs = origTMs - smoothness * timeBias * durMs[k];
                double minTMs = k == 0 ? 0.0 : points[k - 1].TimeMs + 1.0;
                adjustedTMs = Math.Max(adjustedTMs, minTMs);
                float pixelX = _wordLayouts[lineWordBase + local].X - minX;
                float vel = (float)finalVel[k];
                points[k] = new CurvePoint { TimeMs = adjustedTMs, PixelX = pixelX, VelIn = vel, VelOut = vel };
            }

            // 末尾哨兵
            {
                int lastLocal = localIndices[n - 1];
                double endMs = (words[lastLocal].StartTime + words[lastLocal].Duration - lineOrigin).TotalMilliseconds;
                endMs = Math.Max(endMs, points[n - 1].TimeMs + 1.0);
                points[n] = new CurvePoint
                {
                    TimeMs = endMs,
                    PixelX = maxX - minX,
                    VelIn = (float)natVel[n - 1],
                    VelOut = (float)natVel[n - 1],
                };
            }

            return new RowCurve { Origin = lineOrigin, Points = points, Count = n + 1 };
        }

        // ═════════════════════════════════════════════════════════════════════
        // 缓存辅助
        // ═════════════════════════════════════════════════════════════════════

        private void InvalidateLayoutCache()
        {
            _cachedLayoutKey = null;
            _cachedLayoutWidth = 0f;
            _lineLayoutCount = 0;
            _totalWordCount = 0;
            _visualRowCount = 0;
            _rowCurveCount = 0;
            ResetSmoothedRevealX();
            DisposeFmtCache();
        }

        private void ResetSmoothedRevealX()
        {
            for (int i = 0; i < _smoothedRevealX.Length; i++)
                _smoothedRevealX[i] = float.NaN;
        }

        private void EnsureSmoothedRevealXCapacity(int count)
        {
            if (_smoothedRevealX.Length >= count) return;
            _smoothedRevealX = new float[count + 4];
            for (int i = 0; i < _smoothedRevealX.Length; i++)
                _smoothedRevealX[i] = float.NaN;
        }

        private void DisposeFmtCache()
        {
            _lyricsFmt?.Dispose(); _lyricsFmt = null;
            _transFmt?.Dispose(); _transFmt = null;
            _cachedFontFamily = null;
        }

        private CanvasTextFormat GetLyricsFmt()
        {
            float sz = (float)LyricsFontSize;
            string fam = FontFamilyName;
            if (_lyricsFmt is null || _cachedFontFamily != fam || _cachedLyricsFontSizeForFmt != sz)
            {
                _lyricsFmt?.Dispose();
                _lyricsFmt = new CanvasTextFormat
                {
                    FontFamily = fam,
                    FontSize = sz,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = CanvasHorizontalAlignment.Left,
                };
                _cachedLyricsFontSizeForFmt = sz;
            }
            return _lyricsFmt;
        }

        private CanvasTextFormat GetTransFmt()
        {
            float sz = (float)TranslateFontSize;
            string fam = FontFamilyName;
            var align = LyricsTextAlignment;
            if (_transFmt is null || _cachedFontFamily != fam ||
                _cachedTransFontSizeForFmt != sz || _cachedTransAlignmentForFmt != align)
            {
                _transFmt?.Dispose();
                _transFmt = new CanvasTextFormat
                {
                    FontFamily = fam,
                    FontSize = sz,
                    FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 },
                    WordWrapping = CanvasWordWrapping.WholeWord,
                    HorizontalAlignment = align,
                };
                _cachedTransFontSizeForFmt = sz;
                _cachedTransAlignmentForFmt = align;
                _cachedFontFamily = fam;
            }
            return _transFmt;
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

        private static string BuildLayoutKey(ObservableCollection<LyricLine> lyrics)
        {
            var sb = new StringBuilder(lyrics.Count * 8);
            foreach (var line in lyrics)
                sb.Append(line.Words.Count).Append('|')
                  .Append(line.TransLateText?.Length ?? 0).Append(';');
            return sb.ToString();
        }

        private static string BuildFullText(IList<LyricWord> words)
        {
            var sb = new StringBuilder(words.Count * 6);
            foreach (var w in words) sb.Append(w.Word);
            return sb.ToString();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 主绘制：OnDraw
        // ═════════════════════════════════════════════════════════════════════

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            var lyrics = UILyrics;
            if (lyrics is null || lyrics.Count == 0) return;

            float w = (float)sender.ActualWidth;
            if (w <= 0) return;

            // 宽度变化 / 内容变化时在绘制路径重建布局
            if (_lineLayoutCount == 0 || Math.Abs(w - _cachedLayoutWidth) >= 2f)
            {
                float oldH = _totalCanvasHeight;
                EnsureLayout(ds, w);
                if (Math.Abs(_totalCanvasHeight - oldH) > 1f)
                    DispatcherQueue.TryEnqueue(InvalidateMeasure);
            }
            if (_lineLayoutCount == 0) return;

            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Auto;

            var lyricsFmt = GetLyricsFmt();
            var transFmt = GetTransFmt();
            float layoutWidth = Math.Max(1f, w - RenderPaddingH * 2f);
            var effectiveTime = _currentTime - TimeSpan.FromMilliseconds(OffsetMs);
            const float kDeltaSec = 0.016f;

            // 全局视觉行计数器（与 _smoothedRevealX / _rowCurves 索引对齐）
            int globalVrIdx = 0;

            for (int li = 0; li < _lineLayoutCount; li++)
            {
                ref readonly var ll = ref _lineLayouts[li];
                ref readonly var vrr = ref _lineVisualRowRanges[li];
                bool isCurrent = (li == _currentLineIndex);
                var line = lyrics[li];

                // ── 暗色底层 ────────────────────────────────────────────────
                for (int gi = ll.WordStart; gi < ll.WordStart + ll.WordCount; gi++)
                {
                    ref readonly var wl = ref _wordLayouts[gi];
                    if (wl.FullWidth <= 0) continue;
                    ds.DrawText(wl.Text,
                        new Rect(wl.X, wl.Y + ll.OffsetY + PaddingV, wl.FullWidth, wl.Height),
                        _dimColor, lyricsFmt);
                }

                // ── 翻译行 ──────────────────────────────────────────────────
                if (ll.HasTranslate && !string.IsNullOrEmpty(line.TransLateText))
                {
                    ds.DrawText(line.TransLateText,
                        new Rect(RenderPaddingH, ll.OffsetY + ll.TranslateOffsetY, layoutWidth, 9999f),
                        _translateColor, transFmt);
                }

                // ── 当前行：逐字扫光 ────────────────────────────────────────
                if (isCurrent)
                {
                    for (int ri = 0; ri < vrr.Count; ri++)
                    {
                        int gri = vrr.Start + ri;
                        int curveIdx = globalVrIdx + ri;
                        ref readonly var vr = ref _visualRows[gri];

                        float minX = vr.MinX, maxX = vr.MaxX;
                        if (maxX - minX <= 0) continue;

                        int fv = vr.WordStart, lv = vr.WordEnd;
                        while (fv <= lv && _wordLayouts[fv].FullWidth <= 0) fv++;
                        while (lv >= fv && _wordLayouts[lv].FullWidth <= 0) lv--;
                        if (fv > lv) continue;

                        float targetRevealX = (curveIdx < _rowCurveCount && _rowCurves[curveIdx].Count > 0)
                            ? CalcRevealXHermite(ref _rowCurves[curveIdx], effectiveTime, minX, maxX)
                            : CalcRevealXFallback(line.Words, ll.WordStart, fv, lv, minX, effectiveTime);

                        // 平滑追赶
                        float smoothed = curveIdx < _smoothedRevealX.Length
                            ? _smoothedRevealX[curveIdx] : float.NaN;
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
                        else smoothed = targetRevealX;

                        if (curveIdx < _smoothedRevealX.Length)
                            _smoothedRevealX[curveIdx] = smoothed;

                        float revealX = smoothed;
                        float drawY = vr.Y + ll.OffsetY + PaddingV;
                        float rowH = vr.H;
                        float highlightWidth = revealX - minX;

                        // 亮色高亮层
                        if (highlightWidth > 0f)
                        {
                            using (ds.CreateLayer(1f, new Rect(minX, drawY, highlightWidth, rowH)))
                            {
                                for (int gi = fv; gi <= lv; gi++)
                                {
                                    ref readonly var wl = ref _wordLayouts[gi];
                                    if (wl.FullWidth <= 0) continue;
                                    ds.DrawText(wl.Text,
                                        new Rect(wl.X, wl.Y + ll.OffsetY + PaddingV, wl.FullWidth, wl.Height),
                                        _brightColor, lyricsFmt);
                                }
                            }
                        }

                        // 羽化边缘
                        if (revealX > minX && revealX < maxX)
                        {
                            float feather = Math.Min(FeatherWidth, maxX - revealX);
                            if (feather > 0.5f)
                            {
                                if (!_gradStopsValid)
                                {
                                    _gradStops[0] = new CanvasGradientStop { Color = _brightColor, Position = 0f };
                                    _gradStops[1] = new CanvasGradientStop
                                    {
                                        Color = Color.FromArgb(0, _brightColor.R, _brightColor.G, _brightColor.B),
                                        Position = 1f,
                                    };
                                    _gradStopsValid = true;
                                }
                                using var gradBrush = new CanvasLinearGradientBrush(ds, _gradStops)
                                {
                                    StartPoint = new Vector2(revealX, 0f),
                                    EndPoint = new Vector2(revealX + feather, 0f),
                                };
                                using (ds.CreateLayer(gradBrush, new Rect(revealX, drawY, feather, rowH)))
                                {
                                    for (int gi = fv; gi <= lv; gi++)
                                    {
                                        ref readonly var wl = ref _wordLayouts[gi];
                                        if (wl.FullWidth <= 0) continue;
                                        ds.DrawText(wl.Text,
                                            new Rect(wl.X, wl.Y + ll.OffsetY + PaddingV, wl.FullWidth, wl.Height),
                                            _brightColor, lyricsFmt);
                                    }
                                }
                            }
                        }
                    }
                }

                globalVrIdx += vrr.Count;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 插值
        // ═════════════════════════════════════════════════════════════════════

        private static float HermiteInterp(float p0, float p1, float m0, float m1,
            double dt, double elapsed)
        {
            if (dt <= 0) return p1;
            float t = Math.Clamp((float)(elapsed / dt), 0f, 1f);
            float t2 = t * t, t3 = t2 * t;
            return (2 * t3 - 3 * t2 + 1) * p0
                 + (t3 - 2 * t2 + t) * (m0 * (float)dt)
                 + (-2 * t3 + 3 * t2) * p1
                 + (t3 - t2) * (m1 * (float)dt);
        }

        private static float CalcRevealXHermite(ref RowCurve curve,
            TimeSpan effectiveTime, float minX, float maxX)
        {
            if (curve.Count == 0 || curve.Points is null) return minX;
            var pts = curve.Points;
            int ptCount = curve.Count;
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
            float px = HermiteInterp(p0.PixelX, p1.PixelX, p0.VelOut, p1.VelIn,
                                     p1.TimeMs - p0.TimeMs, elapsedMs - p0.TimeMs);
            return minX + Math.Clamp(px, Math.Min(p0.PixelX, p1.PixelX), Math.Max(p0.PixelX, p1.PixelX));
        }

        /// <param name="lineWordBase">该行首字的全局 _wordLayouts 索引</param>
        private float CalcRevealXFallback(IList<LyricWord> words,
            int lineWordBase, int globalFv, int globalLv,
            float minX, TimeSpan effectiveTime)
        {
            int firstLocal = globalFv - lineWordBase;
            int lastLocal = globalLv - lineWordBase;
            if (firstLocal < 0 || lastLocal >= words.Count) return minX;

            if (effectiveTime <= words[firstLocal].StartTime) return minX;

            float totalW = 0f;
            for (int gi = globalFv; gi <= globalLv; gi++)
                totalW += _wordLayouts[gi].FullWidth;

            if (effectiveTime >= words[lastLocal].StartTime + words[lastLocal].Duration)
                return minX + totalW;

            float accX = minX;
            for (int gi = globalFv; gi <= globalLv; gi++)
            {
                int local = gi - lineWordBase;
                ref readonly var wl = ref _wordLayouts[gi];
                if (wl.FullWidth <= 0) continue;
                var word = words[local];
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
    }
}
