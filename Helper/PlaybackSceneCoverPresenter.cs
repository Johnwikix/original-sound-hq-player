using System;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;
using WinUIMusicPlayer.Constants;
using WinUIMusicPlayer.Controls;

namespace WinUIMusicPlayer.Helper;

/// <summary>Draws the current artwork and, during a track change, at most one outgoing texture.</summary>
internal sealed class PlaybackSceneCoverPresenter(TimeProvider? timeProvider = null) : IDisposable
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private PlaybackSceneCover? _artwork;
    private PlaybackSceneCover? _uploadedArtwork;
    private PlaybackSceneCoverDrawing? _current;
    private PlaybackSceneCoverDrawing? _previous;
    private ImageSwitchType _transition;
    private bool _animateNext;
    private long _transitionStarted;

    internal void SetArtwork(PlaybackSceneCover artwork, ImageSwitchType transition, bool animate)
    {
        if (ReferenceEquals(_artwork, artwork)) return;
        _artwork = artwork;
        _transition = transition;
        _animateNext = animate;
    }

    internal bool Draw(CanvasDrawingSession session, Rect slot, int cornerRadiusAmount, int shadowAmount,
        float whiteNits, float peakNits)
    {
        if (_artwork is not { } artwork || slot.Width <= 0 || slot.Height <= 0) return false;
        if (_current is not null && !ReferenceEquals(_current.Device, session.Device)) ReleaseGpu();

        if (_current is null || !ReferenceEquals(_uploadedArtwork, artwork))
        {
            var replacement = new PlaybackSceneCoverDrawing(artwork, session.Device);
            ReleasePrevious();
            if (_animateNext && _current is not null)
            {
                _previous = _current;
                _transitionStarted = _time.GetTimestamp();
            }
            else
            {
                _current?.Dispose();
            }
            _current = replacement;
            _uploadedArtwork = artwork;
            _animateNext = false;
        }

        if (_previous is { } previous)
        {
            TimeSpan elapsed = _time.GetElapsedTime(_transitionStarted);
            if (elapsed < Time.AnimationDuration)
            {
                float progress = EaseTransition((float)(elapsed / Time.AnimationDuration));
                DrawTransition(session, slot, previous, _current, cornerRadiusAmount, shadowAmount, whiteNits, peakNits, progress);
                return true;
            }
            ReleasePrevious();
        }

        DrawArtwork(session, _current, slot, cornerRadiusAmount, shadowAmount, whiteNits, peakNits, 1);
        return true;
    }

    private static float EaseTransition(float elapsed)
    {
        if (elapsed <= 0) return 0;

        // The XAML ScalarTransition and Vector3Transition both use the Fluent standard
        // curve (0.8, 0, 0.2, 1). Solve its time coordinate before evaluating progress.
        float low = 0, high = 1;
        for (int i = 0; i < 14; i++)
        {
            float t = (low + high) * .5f;
            float inverse = 1 - t;
            float x = t * (t * t + inverse * (2.4f * inverse + .6f * t));
            if (x < elapsed)
                low = t;
            else
                high = t;
        }
        float parameter = (low + high) * .5f;
        return parameter * parameter * (3 - 2 * parameter);
    }

    private void DrawTransition(CanvasDrawingSession session, Rect slot, PlaybackSceneCoverDrawing previous,
        PlaybackSceneCoverDrawing current, int radius, int shadow, float whiteNits, float peakNits, float progress)
    {
        float outgoingOpacity = 1 - progress;
        var original = session.Transform;
        try
        {
            if (_transition == ImageSwitchType.Slide)
            {
                // Match the slide direction and travel distance of the XAML artwork.
                session.Transform = Matrix3x2.CreateTranslation((float)-slot.Width * progress, 0) * original;
                DrawArtwork(session, previous, slot, radius, shadow, whiteNits, peakNits, outgoingOpacity);
                session.Transform = Matrix3x2.CreateTranslation((float)-slot.Width * outgoingOpacity, 0) * original;
            }
            else if (_transition == ImageSwitchType.ScaleInOut)
            {
                var center = new Vector2((float)(slot.X + slot.Width * .5), (float)(slot.Y + slot.Height * .5));
                session.Transform = Matrix3x2.CreateScale(1 - .2f * progress, center) * original;
                DrawArtwork(session, previous, slot, radius, shadow, whiteNits, peakNits, outgoingOpacity);
                session.Transform = Matrix3x2.CreateScale(1 - .15f * outgoingOpacity, center) * original;
            }
            else
            {
                DrawArtwork(session, previous, slot, radius, shadow, whiteNits, peakNits, outgoingOpacity);
            }
            DrawArtwork(session, current, slot, radius, shadow, whiteNits, peakNits, progress);
        }
        finally
        {
            session.Transform = original;
        }
    }

    private static void DrawArtwork(CanvasDrawingSession session, PlaybackSceneCoverDrawing drawing, Rect slot,
        int radiusAmount, int shadowAmount, float whiteNits, float peakNits, float opacity)
    {
        if (opacity <= 0) return;
        var bounds = FitArtwork(slot, drawing.ImageSize);
        float radius = (float)(Math.Min(bounds.Width, bounds.Height) * Math.Clamp(radiusAmount, 0, 100) / 200);
        if (shadowAmount > 0) DrawShadow(session, bounds, radius, shadowAmount, opacity);
        drawing.Draw(session, bounds, radius, whiteNits, peakNits, opacity);
    }

    private static Rect FitArtwork(Rect slot, Size source)
    {
        double scale = Math.Min(slot.Width / source.Width, slot.Height / source.Height);
        double width = source.Width * scale;
        double height = source.Height * scale;
        return new Rect(slot.X + (slot.Width - width) * .5, slot.Y + (slot.Height - height) * .5, width, height);
    }

    private static void DrawShadow(CanvasDrawingSession session, Rect bounds, float radius, float depth, float opacity)
    {
        // Concentric rounded fills give the shadow a short falloff without a separate render target.
        const int steps = 8;
        float offset = depth * .25f;
        for (int i = steps; i > 0; i--)
        {
            float fraction = (i - .5f) / steps;
            float spread = depth * fraction;
            float remaining = 1 - fraction;
            var ink = Colors.Black;
            ink.A = (byte)MathF.Round(20 * opacity * remaining * remaining);
            if (ink.A == 0) continue;
            var shadow = new Rect(bounds.X - spread, bounds.Y - spread + offset,
                bounds.Width + spread * 2, bounds.Height + spread * 2);
            session.FillRoundedRectangle(shadow, radius + spread, radius + spread, ink);
        }
    }

    internal void ReleaseGpu()
    {
        ReleasePrevious();
        _current?.Dispose();
        _current = null;
        _uploadedArtwork = null;
        _animateNext = false;
    }

    private void ReleasePrevious()
    {
        _previous?.Dispose();
        _previous = null;
    }

    internal void Clear()
    {
        ReleaseGpu();
        _artwork = null;
    }

    public void Dispose() => Clear();
}
