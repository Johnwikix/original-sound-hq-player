using System;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

        private void LyricsCanvas_RenderError(object sender, Exception e)
        {
            ExceptionInteracted?.Invoke(this,e);
        }
    }
}
