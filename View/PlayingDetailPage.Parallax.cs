using System.Numerics;
using AnimatedWin2dControls.Interop;
using Microsoft.UI.Xaml.Input;

namespace WinUIMusicPlayer.View;

public sealed partial class PlayingDetailPage
{
    private bool IsAutomaticLyricsParallaxEnabled =>
        ViewModel.AppViewModel.Is3DLyricsEnabled && ViewModel.AppViewModel.IsAuto3DLyricsEnabled;

    private void PlaybackParallaxPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!IsAutomaticLyricsParallaxEnabled || ActualWidth <= 0 || ActualHeight <= 0) return;
        using var point = PointerPointScope.GetCurrentPoint(e, this);
        var position = point.Position;
        bool inside = position.X >= 0 && position.X < ActualWidth && position.Y >= 0 && position.Y < ActualHeight;
        SetLyricsParallaxPointer(inside
            ? new Vector2((float)(2 * position.X / ActualWidth - 1), (float)(2 * position.Y / ActualHeight - 1))
            : null);
    }

    private void PlaybackParallaxPointerExited(object sender, PointerRoutedEventArgs e)
    {
        // Handled exits can originate from child controls; keep tracking during transitions within the page.
        PlaybackParallaxPointerMoved(sender, e);
    }

    private void SetLyricsParallaxPointer(Vector2? position)
    {
        NowPlaying?.SetLyricsParallaxPointer(position);
        LyricsView?.SetLyricsParallaxPointer(position);
    }
}
