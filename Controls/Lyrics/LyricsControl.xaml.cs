using System;
using System.Collections.Generic;
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
                typeof(IList<LyricLine>), typeof(LyricsControl),
                new PropertyMetadata(null, (d, e) =>
                {
                    if (d is not LyricsControl c) return;
                    // 直接透传，Canvas 内部会自行重置行索引 / 计时器
                    c.LyricsCanvas.UILyrics = e.NewValue as IList<LyricLine>;
                }));

        public IList<LyricLine>? UILyrics
        {
            get => (IList<LyricLine>?)GetValue(UILyricsProperty);
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
                typeof(LyricsControl), new PropertyMetadata(36.0));

        public double LyricsFontSize
        {
            get => (double)GetValue(LyricsFontSizeProperty);
            set => SetValue(LyricsFontSizeProperty, value);
        }

        public static readonly DependencyProperty LyricsOffsetMsProperty =
            DependencyProperty.Register(nameof(LyricsOffsetMs), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(36.0));

        public double LyricsOffsetMs
        {
            get => (double)GetValue(LyricsOffsetMsProperty);
            set => SetValue(LyricsOffsetMsProperty, value);
        }

        public static readonly DependencyProperty FontFamilyNameProperty =
            DependencyProperty.Register(nameof(FontFamilyName), typeof(string),
                typeof(LyricsControl), new PropertyMetadata("Segoe UI"));

        public string FontFamilyName
        {
            get => (string)GetValue(FontFamilyNameProperty);
            set => SetValue(FontFamilyNameProperty, value);
        }

        public static readonly DependencyProperty LyricsTextAlignmentProperty =
            DependencyProperty.Register(nameof(LyricsTextAlignment),
                typeof(Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment),
                typeof(LyricsControl),
                new PropertyMetadata(Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left));

        public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsTextAlignment
        {
            get => (Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment)GetValue(LyricsTextAlignmentProperty);
            set => SetValue(LyricsTextAlignmentProperty, value);
        }

        public static readonly DependencyProperty IsDarkProperty =
            DependencyProperty.Register(nameof(IsDark), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(false));

        public bool IsDark
        {
            get => (bool)GetValue(IsDarkProperty);
            set => SetValue(IsDarkProperty, value);
        }

        public static readonly DependencyProperty AnimationSmoothnessProperty =
            DependencyProperty.Register(nameof(AnimationSmoothness), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(1.0));

        public double AnimationSmoothness
        {
            get => (double)GetValue(AnimationSmoothnessProperty);
            set => SetValue(AnimationSmoothnessProperty, value);
        }

        public static readonly DependencyProperty LyricsBlurAmountProperty =
                DependencyProperty.Register(nameof(LyricsBlurAmount), typeof(double),
                typeof(UnifiedLyricsCanvasControl), new PropertyMetadata(1.5));

        public double LyricsBlurAmount
        {
            get => (double)GetValue(LyricsBlurAmountProperty);
            set => SetValue(LyricsBlurAmountProperty, Math.Max(0.0, value));
        }

        // ─────────────────────────────────────────────────────────────────────
        // 构造
        // ─────────────────────────────────────────────────────────────────────

        public LyricsControl()
        {
            this.InitializeComponent();
            // Canvas 点击 → 透传 LyricInteracted
            LyricsCanvas.LyricLineClicked += OnCanvasLyricLineClicked;
            Unloaded += (_, _) =>
            {
                LyricsCanvas.LyricLineClicked -= OnCanvasLyricLineClicked;
            };
        }

        private void OnCanvasLyricLineClicked(object? sender, TimeSpan ts)
            => LyricInteracted?.Invoke(this, ts);

    }
}