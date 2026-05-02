using System;
using System.Collections.ObjectModel;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsControl : UserControl
    {
        // ─────────────────────────────────────────────────────────────────────
        // 公开事件
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>用户点击某行歌词时触发，携带该行起始时间戳。</summary>
        public event EventHandler<TimeSpan>? LyricInteracted;

        // ─────────────────────────────────────────────────────────────────────
        // 依赖属性
        // ─────────────────────────────────────────────────────────────────────

        // ── UILyrics ──────────────────────────────────────────────────────────
        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(nameof(UILyrics),
                typeof(ObservableCollection<LyricLine>), typeof(LyricsControl),
                new PropertyMetadata(null, OnUILyricsChanged));

        public ObservableCollection<LyricLine>? UILyrics
        {
            get => (ObservableCollection<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

        private static void OnUILyricsChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not LyricsControl c) return;
            c.LyricsCanvas.UILyrics = e.NewValue as ObservableCollection<LyricLine>;
            c._lastLyricIndex = -1;
            c.LyricsCanvas.CurrentLineIndex = -1;
        }

        // ── LyricsMargin ──────────────────────────────────────────────────────
        public static readonly DependencyProperty LyricsMarginProperty =
            DependencyProperty.Register(nameof(LyricsMargin),
                typeof(Thickness), typeof(LyricsControl),
                new PropertyMetadata(new Thickness(0)));

        public Thickness LyricsMargin
        {
            get => (Thickness)GetValue(LyricsMarginProperty);
            set => SetValue(LyricsMarginProperty, value);
        }

        // ── CurrentPlayingTime ────────────────────────────────────────────────
        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(nameof(CurrentPlayingTime),
                typeof(TimeSpan), typeof(LyricsControl),
                new PropertyMetadata(TimeSpan.Zero, OnCurrentPlayingTimeChanged));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        private static void OnCurrentPlayingTimeChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not LyricsControl c) return;
            var ext = (TimeSpan)e.NewValue;
            // 误差 > 100ms 才强制校准内部时钟，避免外部轮询抖动
            if (Math.Abs((ext - c._internalPosition).TotalMilliseconds) > 150)
                c._internalPosition = ext;
            // 同步透传给画布（画布自己也有独立时钟，但需要外部基准做漂移修正）
            c.LyricsCanvas.CurrentPlayingTime = ext;
        }

        // ── IsPlaying ─────────────────────────────────────────────────────────
        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying),
                typeof(bool), typeof(LyricsControl),
                new PropertyMetadata(false, OnIsPlayingChanged));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        private static void OnIsPlayingChanged(DependencyObject d,
            DependencyPropertyChangedEventArgs e)
        {
            if (d is not LyricsControl c) return;
            c.LyricsCanvas.IsPlaying = (bool)e.NewValue;
            if ((bool)e.NewValue)
                c.StartInternalTimer();
            else
                c.StopInternalTimer();
        }

        // ── 画布视觉属性（透传到 UnifiedLyricsCanvasControl）─────────────────

        public static readonly DependencyProperty LyricsFontSizeProperty =
            DependencyProperty.Register(nameof(LyricsFontSize), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(36.0,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.LyricsFontSize = (double)e.NewValue));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        public static readonly DependencyProperty TranslateFontSizeProperty =
            DependencyProperty.Register(nameof(TranslateFontSize), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(24.0,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.TranslateFontSize = (double)e.NewValue));

        public double TranslateFontSize
        {
            get => (double)GetValue(TranslateFontSizeProperty);
            set => SetValue(TranslateFontSizeProperty, value);
        }

        public static readonly DependencyProperty FontFamilyNameProperty =
            DependencyProperty.Register(nameof(FontFamilyName), typeof(string),
                typeof(LyricsControl), new PropertyMetadata("Segoe UI",
                    (d, e) => ((LyricsControl)d).LyricsCanvas.FontFamilyName = (string)e.NewValue));

        public string FontFamilyName
        {
            get => (string)GetValue(FontFamilyNameProperty);
            set => SetValue(FontFamilyNameProperty, value);
        }

        public static readonly DependencyProperty LyricsTextAlignmentProperty =
            DependencyProperty.Register(nameof(LyricsTextAlignment),
                typeof(Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment),
                typeof(LyricsControl),
                new PropertyMetadata(
                    Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.LyricsTextAlignment =
                        (Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment)e.NewValue));

        public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsTextAlignment
        {
            get => (Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment)
                       GetValue(LyricsTextAlignmentProperty);
            set => SetValue(LyricsTextAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(false,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.IsDark = (bool)e.NewValue));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        public static readonly DependencyProperty AnimationSmoothnessProperty =
            DependencyProperty.Register(nameof(AnimationSmoothness), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.65,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.AnimationSmoothness = (double)e.NewValue));

        public double AnimationSmoothness
        {
            get => (double)GetValue(AnimationSmoothnessProperty);
            set => SetValue(AnimationSmoothnessProperty, value);
        }

        // ─────────────────────────────────────────────────────────────────────
        // 内部高频计时器（歌词匹配）
        // ─────────────────────────────────────────────────────────────────────

        private readonly DispatcherTimer _internalTimer;
        private TimeSpan _internalPosition = TimeSpan.Zero;
        private DateTime _lastTickTime;
        private int _lastLyricIndex = -1;

        private void StartInternalTimer()
        {
            if (_internalTimer.IsEnabled) return;
            _lastTickTime = DateTime.UtcNow;
            _internalTimer.Start();
        }

        private void StopInternalTimer()
        {
            _internalTimer.Stop();
        }

        private void InternalTimer_Tick(object sender, object e)
        {
            var now = DateTime.UtcNow;
            _internalPosition += now - _lastTickTime;
            _lastTickTime = now;
            UpdateLyricsHighlight(_internalPosition);
        }

        // ── 歌词匹配 → 写入 CurrentLineIndex ─────────────────────────────────

        private void UpdateLyricsHighlight(TimeSpan position)
        {
            var lyrics = UILyrics;
            if (lyrics is null || lyrics.Count == 0) return;

            int currentIndex = -1;
            for (int i = 0; i < lyrics.Count; i++)
            {
                if (lyrics[i].Time <= position) currentIndex = i;
                else break;
            }

            if (currentIndex < 0 || currentIndex == _lastLyricIndex) return;

            _lastLyricIndex = currentIndex;
            // 通过依赖属性写入，画布的 OnCurrentLineIndexChanged 自动触发滚动 + 动画
            LyricsCanvas.CurrentLineIndex = currentIndex;
        }

        // ─────────────────────────────────────────────────────────────────────
        // 自动滚动：监听画布的 CurrentLineOffsetYChanged 事件
        // ─────────────────────────────────────────────────────────────────────

        // [修复4] LyricsControl.OnCurrentLineOffsetYChanged：_canvas → LyricsCanvas
        // 同时简化计算：canvasOffsetY 现在已经是行中心，直接减去 ScrollView 高度一半即可
        private void OnCurrentLineOffsetYChanged(object? sender, double canvasOffsetY)
        {
            // canvasOffsetY = 目标行在 LyricsCanvas 内的垂直中心 Y

            // 将 LyricsCanvas 的原点转换到 ScrollView 内容坐标系
            var transform = LyricsCanvas.TransformToVisual(LyricViewer.Content as UIElement ?? LyricsCanvas);
            var canvasOrigin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

            // 目标：让行中心对齐 ScrollView 中央
            double scrollTarget = canvasOrigin.Y + canvasOffsetY - LyricViewer.ActualHeight / 2.0;
            scrollTarget = Math.Max(0, scrollTarget);

            LyricViewer.ScrollTo(
                0, scrollTarget,
                new ScrollingScrollOptions(
                    ScrollingAnimationMode.Enabled,
                    ScrollingSnapPointsMode.Ignore));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 构造
        // ─────────────────────────────────────────────────────────────────────

        public LyricsControl()
        {
            this.InitializeComponent();

            _internalTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33.3) };
            _internalTimer.Tick += InternalTimer_Tick;

            // 画布点击 → 透传 LyricInteracted
            LyricsCanvas.LyricLineClicked += (_, ts) => LyricInteracted?.Invoke(this, ts);

            // 画布当前行 Y 偏移变化 → 自动滚动
            LyricsCanvas.CurrentLineOffsetYChanged += OnCurrentLineOffsetYChanged;

            Unloaded += (_, _) =>
            {
                StopInternalTimer();
                LyricsCanvas.LyricLineClicked -= (_, ts) => LyricInteracted?.Invoke(this, ts);
                LyricsCanvas.CurrentLineOffsetYChanged -= OnCurrentLineOffsetYChanged;
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        // 鼠标滚轮（保持原有行为）
        // ─────────────────────────────────────────────────────────────────────

        private void MaskView_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var pointerPoint = e.GetCurrentPoint(LyricViewer);
            double scrollAmount = -pointerPoint.Properties.MouseWheelDelta * 3;
            LyricViewer.ScrollBy(0, scrollAmount,
                new ScrollingScrollOptions(ScrollingAnimationMode.Enabled));
            e.Handled = true;
        }
    }
}