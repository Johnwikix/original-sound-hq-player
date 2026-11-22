using System;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace WinUIMusicPlayer.View;

internal static class PlaybackLyricsLayout
{
    internal static (Thickness SurfaceMargin, Rect LayoutRegion) Calculate(
        Rect pane, Thickness lyricsMargin, bool portrait, bool hasTransforms)
    {
        double left = Math.Clamp(lyricsMargin.Left, 0, Math.Max(0, pane.Width - 1));
        double right = Math.Clamp(lyricsMargin.Right, 0, Math.Max(0, pane.Width - left - 1));
        double top = Math.Clamp(lyricsMargin.Top, 0, Math.Max(0, pane.Height - 1));
        double bottom = Math.Clamp(lyricsMargin.Bottom, 0, Math.Max(0, pane.Height - top - 1));
        double textLeft = pane.X + left;
        double surfaceLeft = textLeft;
        double surfaceRightMargin = right;

        if (portrait)
        {
            // Leave clear strips for the window backdrop even with a zero lyric margin.
            double edge = Math.Min(8, pane.Width / 4);
            surfaceLeft = Math.Clamp(textLeft, pane.X + edge, Math.Max(pane.X + edge, pane.Right - edge - 1));
            surfaceRightMargin = Math.Clamp(right, edge, Math.Max(edge, pane.Right - surfaceLeft - 1));
        }
        else if (hasTransforms)
        {
            // Keep the landscape surface local to the lyric side of the window.
            surfaceLeft = Math.Max(0, textLeft - pane.Width / 2);
            surfaceRightMargin = 0;
        }

        return (
            new Thickness(surfaceLeft, pane.Y + top, surfaceRightMargin, bottom),
            new Rect(textLeft - surfaceLeft, 0, pane.Width - left - right, pane.Height - top - bottom));
    }
}
