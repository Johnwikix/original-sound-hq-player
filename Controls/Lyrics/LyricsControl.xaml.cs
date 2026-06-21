using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace WinUIMusicPlayer.Controls.Lyrics
{
    public sealed partial class LyricsControl : UserControl
    {
        public event EventHandler<TimeSpan>? LyricInteracted;
        public event EventHandler<Exception>? ExceptionInteracted;

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
            Unloaded += (_, _) =>
            {
                SimpleLyrics?.LyricLineClicked -= OnCanvasLyricLineClicked;
                LyricsCanvas?.LyricLineClicked -= OnCanvasLyricLineClicked;
            };
        }

        private void OnControlLoaded(object sender, RoutedEventArgs e)
        {
            SimpleLyrics?.LyricLineClicked += OnCanvasLyricLineClicked;
            LyricsCanvas?.LyricLineClicked += OnCanvasLyricLineClicked;
            Loaded -= OnControlLoaded;
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
