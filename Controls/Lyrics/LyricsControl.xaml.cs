using System;
using System.Collections.ObjectModel;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    /// <summary>
    /// 歌词外壳控件。
    /// 职责：布局（ScrollView + OpacityMask + 上下占位）、属性透传、滚动响应、鼠标滚轮。
    /// 行匹配、动画帧推进、时钟全部由 UnifiedLyricsCanvasControl 的单一计时器负责。
    /// </summary>
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
                new PropertyMetadata(null, (d, e) =>
                {
                    if (d is not LyricsControl c) return;
                    // 直接透传，Canvas 内部会自行重置行索引 / 计时器
                    c.LyricsCanvas.UILyrics = e.NewValue as ObservableCollection<LyricLine>;
                }));

        public ObservableCollection<LyricLine>? UILyrics
        {
            get => (ObservableCollection<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
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
                new PropertyMetadata(TimeSpan.Zero,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.CurrentPlayingTime = (TimeSpan)e.NewValue));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        // ── IsPlaying ─────────────────────────────────────────────────────────
        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying),
                typeof(bool), typeof(LyricsControl),
                new PropertyMetadata(false,
                    (d, e) => ((LyricsControl)d).LyricsCanvas.IsPlaying = (bool)e.NewValue));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

        // ── 画布视觉属性（透传）──────────────────────────────────────────────

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
            get => (Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment)GetValue(LyricsTextAlignmentProperty);
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
        // 自动滚动：监听 Canvas 的 CurrentLineOffsetYChanged
        // ─────────────────────────────────────────────────────────────────────

        private void OnCurrentLineOffsetYChanged(object? sender, double canvasOffsetY)
        {
            // canvasOffsetY = 目标行在 LyricsCanvas 内的垂直中心 Y
            var transform = LyricsCanvas.TransformToVisual(LyricViewer.Content as UIElement ?? LyricsCanvas);
            var canvasOrigin = transform.TransformPoint(new Windows.Foundation.Point(0, 0));

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

            // Canvas 点击 → 透传 LyricInteracted
            LyricsCanvas.LyricLineClicked += OnCanvasLyricLineClicked;

            // Canvas 行变化 → 自动滚动
            LyricsCanvas.CurrentLineOffsetYChanged += OnCurrentLineOffsetYChanged;

            Unloaded += (_, _) =>
            {
                LyricsCanvas.LyricLineClicked -= OnCanvasLyricLineClicked;
                LyricsCanvas.CurrentLineOffsetYChanged -= OnCurrentLineOffsetYChanged;
            };
        }

        private void OnCanvasLyricLineClicked(object? sender, TimeSpan ts)
            => LyricInteracted?.Invoke(this, ts);

        // ─────────────────────────────────────────────────────────────────────
        // 鼠标滚轮
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