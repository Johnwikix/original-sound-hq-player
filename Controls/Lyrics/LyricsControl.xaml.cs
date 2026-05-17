using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.V2;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsControl : UserControl
    {
        public event EventHandler<TimeSpan>? LyricInteracted;

        public static readonly DependencyProperty EnableAdvancedLyricsProperty =
            DependencyProperty.Register(nameof(EnableAdvancedLyrics), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true));

        public bool EnableAdvancedLyrics
        {
            get => (bool)GetValue(EnableAdvancedLyricsProperty);
            set => SetValue(EnableAdvancedLyricsProperty, value);
        }

        #region Dependency Properties

        public static readonly DependencyProperty UILyricsProperty =
            DependencyProperty.Register(nameof(UILyrics),
                typeof(IList<LyricLine>), typeof(LyricsControl),
                new PropertyMetadata(null));

        public IList<LyricLine>? UILyrics
        {
            get => (IList<LyricLine>?)GetValue(UILyricsProperty);
            set => SetValue(UILyricsProperty, value);
        }

        public static readonly DependencyProperty LyricsMarginProperty =
            DependencyProperty.Register(nameof(LyricsMargin),
                typeof(Thickness), typeof(LyricsControl),
                new PropertyMetadata(new Thickness(0)));

        public Thickness LyricsMargin
        {
            get => (Thickness)GetValue(LyricsMarginProperty);
            set => SetValue(LyricsMarginProperty, value);
        }

        public static readonly DependencyProperty CurrentPlayingTimeProperty =
            DependencyProperty.Register(nameof(CurrentPlayingTime),
                typeof(TimeSpan), typeof(LyricsControl),
                new PropertyMetadata(TimeSpan.Zero));

        public TimeSpan CurrentPlayingTime
        {
            get => (TimeSpan)GetValue(CurrentPlayingTimeProperty);
            set => SetValue(CurrentPlayingTimeProperty, value);
        }

        public static readonly DependencyProperty IsPlayingProperty =
            DependencyProperty.Register(nameof(IsPlaying),
                typeof(bool), typeof(LyricsControl),
                new PropertyMetadata(false));

        public bool IsPlaying
        {
            get => (bool)GetValue(IsPlayingProperty);
            set => SetValue(IsPlayingProperty, value);
        }

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
                typeof(LyricsControl), new PropertyMetadata(0.0));

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

        public static readonly DependencyProperty LyricsBlurAmountProperty =
            DependencyProperty.Register(nameof(LyricsBlurAmount), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(4.0));

        public double LyricsBlurAmount
        {
            get => (double)GetValue(LyricsBlurAmountProperty);
            set => SetValue(LyricsBlurAmountProperty, Math.Max(0.0, value));
        }

        public static readonly DependencyProperty GlowAmountProperty =
            DependencyProperty.Register(nameof(GlowAmount), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.0));

        public double GlowAmount
        {
            get => (double)GetValue(GlowAmountProperty);
            set => SetValue(GlowAmountProperty, value);
        }

        public static readonly DependencyProperty CharFloatAmountProperty =
            DependencyProperty.Register(nameof(CharFloatAmount), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.0));

        public double CharFloatAmount
        {
            get => (double)GetValue(CharFloatAmountProperty);
            set => SetValue(CharFloatAmountProperty, value);
        }

        public static readonly DependencyProperty CharScaleAmountProperty =
            DependencyProperty.Register(nameof(CharScaleAmount), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.0));

        public double CharScaleAmount
        {
            get => (double)GetValue(CharScaleAmountProperty);
            set => SetValue(CharScaleAmountProperty, value);
        }

        public static readonly DependencyProperty LongSyllableThresholdProperty =
            DependencyProperty.Register(nameof(LongSyllableThreshold), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(500.0));

        public double LongSyllableThreshold
        {
            get => (double)GetValue(LongSyllableThresholdProperty);
            set => SetValue(LongSyllableThresholdProperty, value);
        }

        public static readonly DependencyProperty IsFadeOutEnabledProperty =
            DependencyProperty.Register(nameof(IsFadeOutEnabled), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true));

        public bool IsFadeOutEnabled
        {
            get => (bool)GetValue(IsFadeOutEnabledProperty);
            set => SetValue(IsFadeOutEnabledProperty, value);
        }

        public static readonly DependencyProperty IsOutOfSightEnabledProperty =
            DependencyProperty.Register(nameof(IsOutOfSightEnabled), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true));

        public bool IsOutOfSightEnabled
        {
            get => (bool)GetValue(IsOutOfSightEnabledProperty);
            set => SetValue(IsOutOfSightEnabledProperty, value);
        }

        public static readonly DependencyProperty UnplayedOpacityProperty =
            DependencyProperty.Register(nameof(UnplayedOpacity), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.5));

        public double UnplayedOpacity
        {
            get => (double)GetValue(UnplayedOpacityProperty);
            set => SetValue(UnplayedOpacityProperty, value);
        }

        public static readonly DependencyProperty TranslatedOpacityProperty =
            DependencyProperty.Register(nameof(TranslatedOpacity), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.6));

        public double TranslatedOpacity
        {
            get => (double)GetValue(TranslatedOpacityProperty);
            set => SetValue(TranslatedOpacityProperty, value);
        }

        public static readonly DependencyProperty StrokeWidthProperty =
            DependencyProperty.Register(nameof(StrokeWidth), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.0));

        public double StrokeWidth
        {
            get => (double)GetValue(StrokeWidthProperty);
            set => SetValue(StrokeWidthProperty, value);
        }

        public static readonly DependencyProperty ScrollEasingTypeProperty =
            DependencyProperty.Register(nameof(ScrollEasingType), typeof(EasingType),
                typeof(LyricsControl), new PropertyMetadata(EasingType.Sine));

        public EasingType ScrollEasingType
        {
            get => (EasingType)GetValue(ScrollEasingTypeProperty);
            set => SetValue(ScrollEasingTypeProperty, value);
        }

        public static readonly DependencyProperty ScrollEasingModeProperty =
            DependencyProperty.Register(nameof(ScrollEasingMode), typeof(EaseMode),
                typeof(LyricsControl), new PropertyMetadata(EaseMode.Out));

        public EaseMode ScrollEasingMode
        {
            get => (EaseMode)GetValue(ScrollEasingModeProperty);
            set => SetValue(ScrollEasingModeProperty, value);
        }

        public static readonly DependencyProperty PlayingLineTopOffsetProperty =
            DependencyProperty.Register(nameof(PlayingLineTopOffset), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(0.4));

        public double PlayingLineTopOffset
        {
            get => (double)GetValue(PlayingLineTopOffsetProperty);
            set => SetValue(PlayingLineTopOffsetProperty, value);
        }

        public static readonly DependencyProperty TargetFrameRateProperty =
            DependencyProperty.Register(nameof(TargetFrameRate), typeof(double),
                typeof(LyricsControl), new PropertyMetadata(60.0));

        public double TargetFrameRate
        {
            get => (double)GetValue(TargetFrameRateProperty);
            set => SetValue(TargetFrameRateProperty, value);
        }

        #endregion

        public LyricsControl()
        {
            this.InitializeComponent();
            Loaded += OnControlLoaded;
            Unloaded += (_, _) =>
            {
                LyricsCanvasV1?.LyricLineClicked -= OnCanvasLyricLineClicked;
                LyricsCanvas?.LyricLineClicked -= OnCanvasLyricLineClicked;
            };
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            LyricsCanvasV1?.LyricLineClicked += OnCanvasLyricLineClicked;
            LyricsCanvas?.LyricLineClicked += OnCanvasLyricLineClicked;
        }

        private void OnCanvasLyricLineClicked(object? sender, TimeSpan ts)
            => LyricInteracted?.Invoke(this, ts);

        public void ShutdownLyricsCanvas()
        {
            LyricsCanvasV1?.PrepareForShutdown();
            LyricsCanvas?.PrepareForShutdown();
        }
    }
}
