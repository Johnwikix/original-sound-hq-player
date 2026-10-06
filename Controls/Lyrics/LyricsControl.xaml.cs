using AnimatedWin2dControls.Messages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsControl : UserControl
    {
        public event EventHandler<TimeSpan>? LyricInteracted;
        public event EventHandler<Exception>? ExceptionInteracted;
        private SimpleLyricsControl? _subscribedSimpleLyrics;
        private bool _lyricsCanvasEventsAttached;
        private bool _isActive = true;

        public static readonly DependencyProperty IsActiveProperty =
            DependencyProperty.Register(nameof(IsActive), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true, OnActiveChanged));

        public bool IsActive
        {
            get => (bool)GetValue(IsActiveProperty);
            set => SetValue(IsActiveProperty, value);
        }

        public static readonly DependencyProperty EnableAdvancedLyricsProperty =
            DependencyProperty.Register(nameof(EnableAdvancedLyrics), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true, OnAdvancedLyricsChanged));

        public bool EnableAdvancedLyrics
        {
            get => (bool)GetValue(EnableAdvancedLyricsProperty);
            set => SetValue(EnableAdvancedLyricsProperty, value);
        }

        private static void OnAdvancedLyricsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (LyricsControl)d;
            control.UpdateActiveState(control._isActive);
        }

        #region Dependency Properties

        public static readonly DependencyProperty LyricsMarginProperty =
            DependencyProperty.Register(nameof(LyricsMargin),
                typeof(Thickness), typeof(LyricsControl),
                new PropertyMetadata(new Thickness(0)));

        public Thickness LyricsMargin
        {
            get => (Thickness)GetValue(LyricsMarginProperty);
            set => SetValue(LyricsMarginProperty, value);
        }

        #endregion

        public LyricsControl()
        {
            this.InitializeComponent();
            Loaded += OnControlLoaded;
            Unloaded += OnControlUnloaded;
        }

        private static void OnActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
            => ((LyricsControl)d).UpdateActiveState((bool)e.NewValue);

        private void UpdateActiveState(bool isActive)
        {
            _isActive = isActive;
            Opacity = isActive ? 1 : 0;
            IsHitTestVisible = isActive;

            if (LyricsCanvas is not null)
            {
                LyricsCanvas.IsActive = isActive && EnableAdvancedLyrics;
            }
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            AttachSimpleLyrics(SimpleLyrics);

            if (!_lyricsCanvasEventsAttached && LyricsCanvas is not null)
            {
                LyricsCanvas.LyricLineClicked += OnCanvasLyricLineClicked;
                _lyricsCanvasEventsAttached = true;
            }

            LyricsSyncRequestBus.Request();
            UpdateActiveState(_isActive);
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            DetachSimpleLyrics(_subscribedSimpleLyrics);

            if (_lyricsCanvasEventsAttached)
            {
                LyricsCanvas?.LyricLineClicked -= OnCanvasLyricLineClicked;
                _lyricsCanvasEventsAttached = false;
            }
        }

        private void SimpleLyrics_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is SimpleLyricsControl simpleLyrics)
            {
                AttachSimpleLyrics(simpleLyrics);
            }
        }

        private void SimpleLyrics_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is SimpleLyricsControl simpleLyrics)
            {
                DetachSimpleLyrics(simpleLyrics);
            }
        }

        private void AttachSimpleLyrics(SimpleLyricsControl? simpleLyrics)
        {
            if (simpleLyrics is null || ReferenceEquals(_subscribedSimpleLyrics, simpleLyrics))
            {
                return;
            }

            DetachSimpleLyrics(_subscribedSimpleLyrics);
            simpleLyrics.LyricLineClicked += OnCanvasLyricLineClicked;
            _subscribedSimpleLyrics = simpleLyrics;
        }

        private void DetachSimpleLyrics(SimpleLyricsControl? simpleLyrics)
        {
            if (simpleLyrics is null || !ReferenceEquals(_subscribedSimpleLyrics, simpleLyrics))
            {
                return;
            }

            simpleLyrics.LyricLineClicked -= OnCanvasLyricLineClicked;
            _subscribedSimpleLyrics = null;
        }

        private void OnCanvasLyricLineClicked(object? sender, TimeSpan ts)
            => LyricInteracted?.Invoke(this, ts);

        public void ShutdownLyricsCanvas()
        {
            DetachSimpleLyrics(_subscribedSimpleLyrics);
            SimpleLyrics?.PrepareForShutdown();
            LyricsCanvas?.PrepareForShutdown();
        }

        public void PauseRendering() => LyricsCanvas?.PauseRendering();
        public void ResumeRendering() => LyricsCanvas?.ResumeRendering();
        public void SetWindowPaused(bool paused) => LyricsCanvas?.SetWindowPaused(paused);

        private void LyricsCanvas_RenderError(object sender, Exception e)
        {
            ExceptionInteracted?.Invoke(this, e);
        }
    }
}
