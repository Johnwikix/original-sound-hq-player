using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Numerics;
using System.Text;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl
{
    public sealed class UnifiedLyricsCanvasControl : Control
    {
        // ─────────────────────────────────────────────────────────────────────
        // 公开事件
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>点击某行歌词时触发，携带该行起始时间戳。</summary>
        public event EventHandler<TimeSpan>? LyricLineClicked;

        /// <summary>
        /// 当前播放行的 Y 偏移（相对歌词坐标系顶部，单位 px）发生变化时触发。
        /// 外部如仍需监听可订阅此事件；内部自动滚动已内置，无需 ScrollView。
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

        private struct LineLayout
        {
            public int WordStart;
            public int WordCount;
            public float OffsetY;   // 歌词坐标系 Y（不含上方 padding）
            public float Height;
            public float TranslateOffsetY;
            public bool HasTranslate;
        }

        private struct VisualRow
        {
            public float MinX, MaxX, Y, H;
            public int WordStart, WordEnd;
        }

        private struct LineVisualRowRange
        {
            public int Start, Count;
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
        private float _totalCanvasHeight = 0f;  // 全部歌词行高度之和（不含 ViewPadding）

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

        private RowCurve[] _rowCurves = [];
        private int _rowCurveCount = 0;

        // ─────────────────────────────────────────────────────────────────────
        // 逐字动画平滑追赶
        // ─────────────────────────────────────────────────────────────────────
        private float[] _smoothedRevealX = [];
        private const float SmoothLerpSpeed = 18f;
        private const float SmoothMaxPixelsPerSec = 2000f;

        // ─────────────────────────────────────────────────────────────────────
        // 内置滚动状态
        //
        // 坐标约定（唯一，所有计算以此为准）：
        //   _smoothedScrollY  = 当前"视口中心"在歌词坐标系中的 Y 值
        //   渲染时：ds.Transform = Translate(0,  viewH/2 - _smoothedScrollY)
        //   即：歌词坐标 Y=_smoothedScrollY 的点，恰好显示在视口纵向中央
        //
        // 可滚动范围：
        //   min = -VScrollPadding          (第一行能滚到中央，再多留一点)
        //   max = _totalCanvasHeight + VScrollPadding  (最后一行能滚到中央)
        // ─────────────────────────────────────────────────────────────────────

        // 上下固定 padding（px），与窗口高度无关
        private const float VScrollPadding = 300f;

        private double _targetScrollY = 0.0;
        private double _smoothedScrollY = 0.0;
        private bool _userScrolling = false;
        private double _userScrollCooldownSec = 0.0;
        private const double UserScrollCooldown = 2.5;

        // ── 可调滚动速度 ──────────────────────────────────────────────────────
        /// <summary>自动行追踪的弹簧速度（越大越快跟上，推荐 6~12）</summary>
        public double AutoScrollSpeed = 4.0;
        /// <summary>用户手势松手后回弹到当前行的弹簧速度（推荐 10~20）</summary>
        public double UserScrollReturnSpeed = 12.0;
        /// <summary>鼠标滚轮每格滚动的像素量（推荐 80~200）</summary>
        public double WheelScrollPixels = 80.0;

        // 触摸 / 鼠标 Pan
        private bool _pointerCaptured = false;
        private double _pointerLastY = 0.0;
        private double _pointerVelocityY = 0.0;
        private double _flingY = 0.0;
        private const double FlingDecay = 0.92;
        private const double FlingStopThreshold = 5.0;

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
        private double _currentLineOffsetY = 0.0;   // 歌词坐标系中当前行中心 Y

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
        // CanvasTextFormat 缓存
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
                _canvas.PointerWheelChanged -= OnPointerWheelChanged;
                _canvas.PointerPressed -= OnPointerPressed;
                _canvas.PointerMoved -= OnPointerMoved;
                _canvas.PointerReleased -= OnPointerReleased;
                _canvas.PointerCanceled -= OnPointerReleased;
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
                _canvas.PointerWheelChanged -= OnPointerWheelChanged;
                _canvas.PointerPressed -= OnPointerPressed;
                _canvas.PointerMoved -= OnPointerMoved;
                _canvas.PointerReleased -= OnPointerReleased;
                _canvas.PointerCanceled -= OnPointerReleased;
            }
            _canvas = GetTemplateChild("PART_Canvas") as CanvasControl;
            if (_canvas is not null)
            {
                _canvas.Draw += OnDraw;
                _canvas.SizeChanged += OnCanvasSizeChanged;
                _canvas.CreateResources += OnCreateResources;
                _canvas.Tapped += OnCanvasTapped;
                _canvas.PointerWheelChanged += OnPointerWheelChanged;
                _canvas.PointerPressed += OnPointerPressed;
                _canvas.PointerMoved += OnPointerMoved;
                _canvas.PointerReleased += OnPointerReleased;
                _canvas.PointerCanceled += OnPointerReleased;
                _canvas.ClearColor = Colors.Transparent;
                _canvas.ManipulationMode = ManipulationModes.None; // 手势由 Pointer 事件手动处理
            }
            UpdateColors(IsDark);
            UpdateTimerState();
        }

        // ─────────────────────────────────────────────────────────────────────
        // MeasureOverride：Canvas 固定铺满父级，不再根据内容撑高
        // ─────────────────────────────────────────────────────────────────────

        protected override Size MeasureOverride(Size availableSize)
        {
            // 返回 (0,0)，让父级（Grid/Page 等）决定控件尺寸
            // 布局计算仍在 OnDraw 首帧触发
            return new Size(0, 0);
        }

        // ═════════════════════════════════════════════════════════════════════
        // 依赖属性
        // ═════════════════════════════════════════════════════════════════════

        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(nameof(UILyrics),
                typeof(ObservableCollection<LyricLine>), typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(null, (d, _) =>
                {
                    if (d is not UnifiedLyricsCanvasControl c) return;
                    c._currentLineIndex = -1;
                    c._currentTime = TimeSpan.Zero;
                    c._lastExternalTime = TimeSpan.Zero;
                    c._targetScrollY = 0;
                    c._smoothedScrollY = 0;
                    c._flingY = 0;
                    c.DestroyTimer();
                    c.InvalidateLayoutCache();
                    c._canvas?.Invalidate();
                }));

        public ObservableCollection<LyricLine>? UILyrics
        {
            get => (ObservableCollection<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

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
                c.MatchLyricLine(ext);
                c._canvas?.Invalidate();
                return;
            }

            if (Math.Abs((ext - c._currentTime).TotalMilliseconds) > SyncThresholdMs)
            {
                c._currentTime = ext;
                c._lastTickAt = DateTimeOffset.UtcNow;
                c.ResetSmoothedRevealX();
                c.MatchLyricLine(ext);
                c._canvas?.Invalidate();
            }
        }

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
            if (c._isPlaying && c._timer is not null)
                c._lastTickAt = DateTimeOffset.UtcNow;
            c.UpdateTimerState();
        }

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

        public static readonly DependencyProperty OffsetMsProperty =
            DependencyProperty.Register(nameof(OffsetMs), typeof(double),
                typeof(UnifiedLyricsCanvasControl),
                new PropertyMetadata(0.0, (d, _) => (d as UnifiedLyricsCanvasControl)?._canvas?.Invalidate()));

        public double OffsetMs
        {
            get => (double)GetValue(OffsetMsProperty);
            set => SetValue(OffsetMsProperty, value);
        }

        // 只读：歌词坐标系中当前行中心 Y
        private static readonly DependencyProperty CurrentLineOffsetYProperty =
            DependencyProperty.Register(nameof(CurrentLineOffsetY), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(0.0));

        public double CurrentLineOffsetY
        {
            get => (double)GetValue(CurrentLineOffsetYProperty);
            private set => SetValue(CurrentLineOffsetYProperty, value);
        }

        // ── 滚动灵敏度倍率（鼠标滚轮，默认 1.0）────────────────────────────
        public static readonly DependencyProperty ScrollSensitivityProperty =
            DependencyProperty.Register(nameof(ScrollSensitivity), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(1.0));

        public double ScrollSensitivity
        {
            get => (double)GetValue(ScrollSensitivityProperty);
            set => SetValue(ScrollSensitivityProperty, Math.Clamp(value, 0.1, 10.0));
        }

        private static void OnLayoutPropertyChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not UnifiedLyricsCanvasControl c) return;
            c.InvalidateLayoutCache();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 输入：鼠标滚轮
        // ═════════════════════════════════════════════════════════════════════

        private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pp = e.GetCurrentPoint(_canvas);
            // MouseWheelDelta 通常为 ±120，归一化为 ±1 格后乘像素量
            double delta = -(pp.Properties.MouseWheelDelta / 120.0) * WheelScrollPixels * ScrollSensitivity;
            // 立即移动（_smoothedScrollY 同步），不走平滑追赶延迟
            _targetScrollY = ClampScrollY(_targetScrollY + delta);
            _smoothedScrollY = _targetScrollY;
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            _canvas?.Invalidate();
            e.Handled = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 输入：触摸 / 鼠标拖拽 Pan
        // ─────────────────────────────────────────────────────────────────────

        private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_canvas is null) return;
            _canvas.CapturePointer(e.Pointer);
            _pointerCaptured = true;
            _pointerLastY = e.GetCurrentPoint(_canvas).Position.Y;
            _pointerVelocityY = 0;
            _flingY = 0;
            _userScrolling = true;
            _userScrollCooldownSec = 0;
            e.Handled = true;
        }

        private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerCaptured || _canvas is null) return;
            double y = e.GetCurrentPoint(_canvas).Position.Y;
            double dy = _pointerLastY - y;   // 向下拖 → 负数 → 视图向下 = scrollY 减小
            _pointerLastY = y;
            _pointerVelocityY = dy;          // 粗略速度（px/frame），OnDraw 换算成 px/s
            ApplyUserScroll(dy);
            e.Handled = true;
        }

        private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_pointerCaptured || _canvas is null) return;
            _canvas.ReleasePointerCapture(e.Pointer);
            _pointerCaptured = false;
            // 启动惯性：速度换算成 px/s（假设 60 fps → 16ms/frame）
            _flingY = _pointerVelocityY * 60.0;
            _userScrollCooldownSec = UserScrollCooldown;
            e.Handled = true;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 公共：外部手动将某个歌词坐标 Y 滚到视口中央
        // ─────────────────────────────────────────────────────────────────────
        public void ScrollToLyricsY(double lyricsY, bool animate = true)
        {
            _targetScrollY = ClampScrollY(lyricsY);
            if (!animate) _smoothedScrollY = _targetScrollY;
            _canvas?.Invalidate();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 内部：用户手势写入滚动偏移
        // ─────────────────────────────────────────────────────────────────────
        private void ApplyUserScroll(double deltaY)
        {
            _targetScrollY = ClampScrollY(_targetScrollY + deltaY);
            _userScrolling = true;
            _userScrollCooldownSec = UserScrollCooldown;
            _canvas?.Invalidate();
        }

        // _smoothedScrollY = 视口中心对应的歌词坐标 Y
        // min：第一行中心可到视口中心（歌词 Y=0 处），再留 VScrollPadding
        // max：最后一行中心可到视口中心，再留 VScrollPadding
        private double ClampScrollY(double y)
        {
            double minY = -VScrollPadding;
            double maxY = _totalCanvasHeight + VScrollPadding;
            return Math.Clamp(y, minY, Math.Max(minY, maxY));
        }

        // ═════════════════════════════════════════════════════════════════════
        // 歌词行匹配
        // ═════════════════════════════════════════════════════════════════════

        private void MatchLyricLine(TimeSpan position)
        {
            var lyrics = UILyrics;
            if (lyrics is null || lyrics.Count == 0) return;

            int matched = -1;
            for (int i = 0; i < lyrics.Count; i++)
            {
                if (lyrics[i].Time <= position) matched = i;
                else break;
            }

            if (matched == _currentLineIndex) return;

            _currentLineIndex = matched;
            ResetSmoothedRevealX();
            UpdateTimerState();
            UpdateCurrentLineOffsetY();
            // 自动滚动：仅在用户未手动滚动时触发
            if (!_userScrolling) AutoScrollToCurrentLine();
        }

        // ─────────────────────────────────────────────────────────────────────
        // 自动滚动：把目标行中心 Y 设为视口中心
        // ─────────────────────────────────────────────────────────────────────
        private void AutoScrollToCurrentLine()
        {
            if (_currentLineIndex < 0 || _currentLineIndex >= _lineLayoutCount) return;
            ref readonly var ll = ref _lineLayouts[_currentLineIndex];
            // 行中心 Y（歌词坐标系）直接就是 _targetScrollY
            double lineCenter = ll.OffsetY + ll.Height / 2.0;
            _targetScrollY = ClampScrollY(lineCenter);
        }

        // ═════════════════════════════════════════════════════════════════════
        // 时钟
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateTimerState()
        {
            var lyrics = UILyrics;
            bool shouldRun = _isPlaying && lyrics != null && lyrics.Count > 0;
            if (shouldRun)
            {
                if (_timer is null) CreateTimer();
                if (!_timer!.IsEnabled)
                {
                    _lastTickAt = DateTimeOffset.UtcNow;
                    _timer.Start();
                }
            }
            else
            {
                _timer?.Stop();
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
            MatchLyricLine(_currentTime);
            _canvas?.Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 设备 / 尺寸
        // ═════════════════════════════════════════════════════════════════════

        private void OnCreateResources(CanvasControl sender,
            Microsoft.Graphics.Canvas.UI.CanvasCreateResourcesEventArgs args)
        {
            DisposeFmtCache();
            InvalidateLayoutCache();
        }

        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
        {
            InvalidateLayoutCache();
            _canvas?.Invalidate();
        }

        // ═════════════════════════════════════════════════════════════════════
        // 点击命中测试（坐标需加上 _smoothedScrollY）
        // ═════════════════════════════════════════════════════════════════════

        private void OnCanvasTapped(object sender, TappedRoutedEventArgs e)
        {
            if (_lineLayoutCount == 0 || _canvas is null) return;
            double viewH = _canvas.ActualHeight;
            float tapViewY = (float)e.GetPosition(_canvas).Y;
            float tapY = (float)(tapViewY - viewH / 2.0 + _smoothedScrollY);

            for (int li = 0; li < _lineLayoutCount; li++)
            {
                ref readonly var ll = ref _lineLayouts[li];
                if (tapY >= ll.OffsetY && tapY < ll.OffsetY + ll.Height)
                {
                    var lyrics = UILyrics;
                    if (lyrics != null && li < lyrics.Count)
                    {
                        // 立即解除用户滚动锁，点击后自动滚动动画马上接管
                        _userScrolling = false;
                        _userScrollCooldownSec = 0;
                        _flingY = 0;
                        LyricLineClicked?.Invoke(this, lyrics[li].Time);
                    }
                    return;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // CurrentLineOffsetY
        // ═════════════════════════════════════════════════════════════════════

        private void UpdateCurrentLineOffsetY()
        {
            double newY = (_currentLineIndex >= 0 && _currentLineIndex < _lineLayoutCount)
                ? _lineLayouts[_currentLineIndex].OffsetY + _lineLayouts[_currentLineIndex].Height / 2.0
                : 0.0;

            if (Math.Abs(newY - _currentLineOffsetY) > 0.5)
            {
                _currentLineOffsetY = newY;
                CurrentLineOffsetY = newY;
                CurrentLineOffsetYChanged?.Invoke(this, newY);
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // 布局计算
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

            string key = $"{lyrics.Count}:{BuildLayoutKey(lyrics)}";
            if (key == _cachedLayoutKey && Math.Abs(availableWidth - _cachedLayoutWidth) < 1f)
                return;

            int lineCount = lyrics.Count;
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
                    using var transLayout = new CanvasTextLayout(
                        creator, line.TransLateText, transFmtTmp, layoutWidth, 9999f);
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

            // 布局重建后，如果当前行有效则立即定位（不动画跳转到位）
            if (_currentLineIndex >= 0 && !_userScrolling)
            {
                AutoScrollToCurrentLine();
                _smoothedScrollY = _targetScrollY;
            }
        }

        // ─────────────────────────────────────────────────────────────────────
        // 视觉行预计算
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
        // 速度曲线
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

        private RowCurve BuildSingleRowCurve(
            IList<LyricWord> words, int lineWordBase,
            int globalFv, int globalLv,
            float minX, float maxX, double smoothness)
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

            Span<double> natVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++) natVel[k] = pxWidths[k] / durMs[k];

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

            const double maxRatio = 1.8;
            for (int k = 0; k < n; k++)
                catmullVel[k] = Math.Clamp(catmullVel[k], 0.0, natVel[k] * maxRatio);

            Span<double> finalVel = n <= 64 ? stackalloc double[n] : new double[n];
            for (int k = 0; k < n; k++)
                finalVel[k] = natVel[k] + smoothness * (catmullVel[k] - natVel[k]);

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
        // OnDraw：虚拟化渲染 + 内置滚动步进
        // ═════════════════════════════════════════════════════════════════════

        private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            var lyrics = UILyrics;
            if (lyrics is null || lyrics.Count == 0) return;

            float w = (float)sender.ActualWidth;
            float viewH = (float)sender.ActualHeight;
            if (w <= 0 || viewH <= 0) return;

            // ── 布局（懒加载）───────────────────────────────────────────────
            if (_lineLayoutCount == 0 || Math.Abs(w - _cachedLayoutWidth) >= 2f)
            {
                EnsureLayout(sender, w);
                if (_lineLayoutCount == 0) return;
            }

            // ── 每帧滚动步进 ─────────────────────────────────────────────────
            const float dt = 0.016f;

            // 1. 用户冷却倒计时
            if (_userScrolling && !_pointerCaptured)
            {
                _userScrollCooldownSec -= dt;
                if (_userScrollCooldownSec <= 0)
                {
                    _userScrolling = false;
                    _flingY = 0;
                    // 冷却结束后立即回到当前行
                    if (_currentLineIndex >= 0) AutoScrollToCurrentLine();
                }
            }

            // 2. 惯性
            if (!_pointerCaptured && Math.Abs(_flingY) > FlingStopThreshold)
            {
                double flingDelta = _flingY * dt;
                _targetScrollY = ClampScrollY(_targetScrollY + flingDelta);
                _flingY *= Math.Pow(FlingDecay, dt * 60.0);
            }
            else if (!_pointerCaptured)
            {
                _flingY = 0;
            }

            // 3. 平滑追赶 _smoothedScrollY → _targetScrollY
            double scrollSpeed = _userScrolling ? UserScrollReturnSpeed : AutoScrollSpeed;
            double scrollDiff = _targetScrollY - _smoothedScrollY;
            if (Math.Abs(scrollDiff) > 0.5)
            {
                double lf = 1.0 - Math.Exp(-scrollSpeed * dt);
                _smoothedScrollY += scrollDiff * lf;
                if (Math.Abs(_targetScrollY - _smoothedScrollY) < 0.5)
                    _smoothedScrollY = _targetScrollY;
                sender.Invalidate();
            }
            else
            {
                _smoothedScrollY = _targetScrollY;
            }

            // ── 坐标变换 ─────────────────────────────────────────────────────
            // 约定：歌词坐标 Y = _smoothedScrollY 的点显示在视口纵向中央
            //   视口 Y = 歌词坐标 Y - _smoothedScrollY + viewH/2
            //   即 ds.Transform.dy = viewH/2 - _smoothedScrollY
            float translateY = viewH / 2f - (float)_smoothedScrollY;
            ds.Transform = Matrix3x2.CreateTranslation(0f, translateY);

            ds.Antialiasing = CanvasAntialiasing.Antialiased;
            ds.TextAntialiasing = CanvasTextAntialiasing.Auto;

            var lyricsFmt = GetLyricsFmt();
            var transFmt = GetTransFmt();
            float layoutWidth = Math.Max(1f, w - RenderPaddingH * 2f);
            var effectiveTime = _currentTime - TimeSpan.FromMilliseconds(OffsetMs);

            // 视口在歌词坐标系中可见的 Y 范围
            // 视口顶部 = _smoothedScrollY - viewH/2，底部 = _smoothedScrollY + viewH/2
            float viewTop = (float)_smoothedScrollY - viewH / 2f;
            float viewBot = (float)_smoothedScrollY + viewH / 2f;

            int globalVrIdx = 0;

            for (int li = 0; li < _lineLayoutCount; li++)
            {
                ref readonly var ll = ref _lineLayouts[li];
                ref readonly var vrr = ref _lineVisualRowRanges[li];

                // ── 视口裁剪 ────────────────────────────────────────────────
                float lineTop = ll.OffsetY;
                float lineBot = ll.OffsetY + ll.Height;
                if (lineBot < viewTop || lineTop > viewBot)
                {
                    globalVrIdx += vrr.Count;
                    continue;
                }

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

                // ── 当前行逐字扫光 ──────────────────────────────────────────
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

                        float smoothed = curveIdx < _smoothedRevealX.Length
                            ? _smoothedRevealX[curveIdx] : float.NaN;
                        if (float.IsNaN(smoothed)) smoothed = targetRevealX;

                        float diff = targetRevealX - smoothed;
                        if (Math.Abs(diff) > 0.5f)
                        {
                            float lf = 1f - MathF.Exp(-SmoothLerpSpeed * dt);
                            float step = diff * lf;
                            float maxStep = SmoothMaxPixelsPerSec * dt;
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

                        if (revealX > minX && revealX < maxX)
                        {
                            float feather = Math.Min(FeatherWidth, maxX - revealX);
                            if (feather > 0.5f)
                            {
                                if (!_gradStopsValid)
                                {
                                    _gradStops[0] = new CanvasGradientStop
                                    { Color = _brightColor, Position = 0f };
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

            // Transform 复原（DrawingSession 销毁时也会自动复原，显式写出更清晰）
            ds.Transform = Matrix3x2.Identity;
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