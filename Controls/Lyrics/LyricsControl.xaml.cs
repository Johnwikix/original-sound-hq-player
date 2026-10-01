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
        private bool _eventsAttached;

        public static readonly DependencyProperty EnableAdvancedLyricsProperty =
            DependencyProperty.Register(nameof(EnableAdvancedLyrics), typeof(bool),
                typeof(LyricsControl), new PropertyMetadata(true));

        public bool EnableAdvancedLyrics
        {
            get => (bool)GetValue(EnableAdvancedLyricsProperty);
            set => SetValue(EnableAdvancedLyricsProperty, value);
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

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            if (_eventsAttached) return;

            SimpleLyrics?.LyricLineClicked += OnCanvasLyricLineClicked;
            LyricsCanvas?.LyricLineClicked += OnCanvasLyricLineClicked;
            _eventsAttached = true;
            LyricsSyncRequestBus.Request();
        }

        private void OnControlUnloaded(object sender, RoutedEventArgs e)
        {
            if (!_eventsAttached) return;

            SimpleLyrics?.LyricLineClicked -= OnCanvasLyricLineClicked;
            LyricsCanvas?.LyricLineClicked -= OnCanvasLyricLineClicked;
            _eventsAttached = false;
        }

        private void OnCanvasLyricLineClicked(object? sender, TimeSpan ts)
            => LyricInteracted?.Invoke(this, ts);

        public void ShutdownLyricsCanvas()
        {
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
