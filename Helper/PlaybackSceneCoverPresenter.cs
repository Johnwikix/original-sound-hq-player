using System;
using System.Numerics;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;
using WinUIMusicPlayer.Constants;
using WinUIMusicPlayer.Controls;

namespace WinUIMusicPlayer.Helper;

/// <summary>Draws the current artwork and, during a track change, at most one outgoing texture.</summary>
internal sealed class PlaybackSceneCoverPresenter(TimeProvider? timeProvider = null) : IPlaybackSceneCover, IDisposable
{
    private sealed record ArtworkRequest(PlaybackSceneCover Artwork, ImageSwitchType Transition, bool Animate, object Track);
    private sealed record CoverLayout(Rect Slot, int Radius, int Shadow);

    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private volatile ArtworkRequest? _requested;
    private volatile CoverLayout? _layout;
    private ArtworkRequest? _active;
    private PlaybackSceneCover? _outgoingArtwork;
    private PlaybackSceneCoverDrawing? _current;
    private PlaybackSceneCoverDrawing? _previous;
    private CanvasDevice? _device;
    private ArtworkRequest? _failedSdrRequest;
    private long _transitionStarted;

    // The UI publishes immutable inputs. NowPlayingCanvas serializes drawing and resource release.
    internal PlaybackSceneCover? RequestedArtwork => _requested?.Artwork;

    internal void SetArtwork(PlaybackSceneCover artwork, ImageSwitchType transition, bool animate, object? track = null)
    {
        if (ReferenceEquals(_requested?.Artwork, artwork)) return;
        _requested = new(artwork, transition, animate, track ?? artwork);
    }

    internal void SetLayout(Rect slot, int radius, int shadow)
    {
        if (_layout is { } layout && layout.Slot == slot && layout.Radius == radius && layout.Shadow == shadow) return;
        _layout = new(slot, radius, shadow);
    }

    bool IPlaybackSceneCover.Draw(PlaybackCanvasFrame frame, CanvasDrawContext session)
    {
        if (_layout is not { } layout || _requested is not { } request) return false;
        if (!frame.Hdr.IsEnabled && ReferenceEquals(_device, frame.Device) && ReferenceEquals(_failedSdrRequest, request))
            return false;
        try
        {
            return Draw(session, frame.Device, layout.Slot, layout.Radius, layout.Shadow, frame.Hdr, request);
        }
        catch (Exception ex) when (!frame.Hdr.IsEnabled && !frame.Device.IsDeviceLost(ex.HResult))
        {
            System.Diagnostics.Trace.TraceWarning("Unable to draw the SDR playback cover: {0}", ex);
            ReleaseGpu();
            _active = null;
            _outgoingArtwork = null;
            _device = frame.Device;
            _failedSdrRequest = request;
            return false;
        }
    }

    void IPlaybackSceneCover.ReleaseDeviceResources(CanvasDevice? device)
    {
        if (device is null || ReferenceEquals(_device, device)) ReleaseGpu();
    }

    internal bool Draw(CanvasDrawingSession session, Rect slot, int cornerRadiusAmount, int shadowAmount,
        float whiteNits, float peakNits)
    {
        if (_requested is not { } request) return false;
        using var scope = CanvasDrawingSessionScope.Borrow(session);
        return Draw(scope.Context, session.Device, slot, cornerRadiusAmount, shadowAmount,
            new PlaybackHdrColor(whiteNits / 80f, peakNits / 80f), request);
    }

    private bool Draw(CanvasDrawContext session, CanvasDevice device, Rect slot, int radius, int shadow,
        PlaybackHdrColor color, ArtworkRequest request)
    {
        if (slot.Width <= 0 || slot.Height <= 0) return false;
        if (!ReferenceEquals(_device, device))
        {
            ReleaseGpu();
            _device = device;
        }

        if (!ReferenceEquals(_active, request))
        {
            _failedSdrRequest = null;
            // Replacing the current track's resolution leaves a running transition intact.
            if (ReferenceEquals(_active?.Track, request.Track))
            {
                _current?.Dispose();
            }
            else
            {
                ReleasePrevious();
                if (request.Animate && _active is { } active)
                {
                    _outgoingArtwork = active.Artwork;
                    _previous = _current;
                    _transitionStarted = _time.GetTimestamp();
                }
                else
                {
                    _current?.Dispose();
                }
            }
            _current = null;
            _active = request;
        }

        var current = _current ??= new PlaybackSceneCoverDrawing(request.Artwork, device);
        if (_outgoingArtwork is { } outgoing)
        {
            TimeSpan elapsed = _time.GetElapsedTime(_transitionStarted);
            if (elapsed < Time.AnimationDuration)
            {
                var previous = _previous ??= new PlaybackSceneCoverDrawing(outgoing, device);
                float progress = EaseTransition((float)(elapsed / Time.AnimationDuration));
                DrawTransition(session, slot, previous, current, radius, shadow, color, request.Transition, progress);
                return true;
            }
            ReleasePrevious();
        }

        DrawArtwork(session, current, slot, radius, shadow, color, 1);
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

    private static void DrawTransition(CanvasDrawContext session, Rect slot, PlaybackSceneCoverDrawing previous,
        PlaybackSceneCoverDrawing current, int radius, int shadow, PlaybackHdrColor color, ImageSwitchType transition, float progress)
    {
        float outgoingOpacity = 1 - progress;
        var original = session.Transform;
        try
        {
            if (transition == ImageSwitchType.Slide)
            {
                // Match the slide direction and travel distance of the XAML artwork.
                session.Transform = Matrix3x2.CreateTranslation((float)-slot.Width * progress, 0) * original;
                DrawArtwork(session, previous, slot, radius, shadow, color, outgoingOpacity);
                session.Transform = Matrix3x2.CreateTranslation((float)-slot.Width * outgoingOpacity, 0) * original;
            }
            else if (transition == ImageSwitchType.ScaleInOut)
            {
                var center = new Vector2((float)(slot.X + slot.Width * .5), (float)(slot.Y + slot.Height * .5));
                session.Transform = Matrix3x2.CreateScale(1 - .2f * progress, center) * original;
                DrawArtwork(session, previous, slot, radius, shadow, color, outgoingOpacity);
                session.Transform = Matrix3x2.CreateScale(1 - .15f * outgoingOpacity, center) * original;
            }
            else
            {
                DrawArtwork(session, previous, slot, radius, shadow, color, outgoingOpacity);
            }
            DrawArtwork(session, current, slot, radius, shadow, color, progress);
        }
        finally
        {
            session.Transform = original;
        }
    }

    private static void DrawArtwork(CanvasDrawContext session, PlaybackSceneCoverDrawing drawing, Rect slot,
        int radiusAmount, int shadowAmount, PlaybackHdrColor color, float opacity)
    {
        if (opacity <= 0) return;
        var bounds = FitArtwork(slot, drawing.ImageSize);
        float radius = (float)(Math.Min(bounds.Width, bounds.Height) * Math.Clamp(radiusAmount, 0, 100) / 200);
        if (shadowAmount > 0) DrawShadow(session, bounds, radius, shadowAmount, opacity);
        drawing.Draw(session, bounds, radius, color, opacity);
    }

    private static Rect FitArtwork(Rect slot, Size source)
    {
        double scale = Math.Min(slot.Width / source.Width, slot.Height / source.Height);
        double width = source.Width * scale;
        double height = source.Height * scale;
        return new Rect(slot.X + (slot.Width - width) * .5, slot.Y + (slot.Height - height) * .5, width, height);
    }

    private static void DrawShadow(CanvasDrawContext session, Rect bounds, float radius, float depth, float opacity)
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
        var previous = _previous;
        var current = _current;
        _previous = null;
        _current = null;
        _device = null;
        _failedSdrRequest = null;
        try { previous?.Dispose(); }
        finally { current?.Dispose(); }
    }

    private void ReleasePrevious()
    {
        var previous = _previous;
        _previous = null;
        _outgoingArtwork = null;
        previous?.Dispose();
    }

    internal void Clear()
    {
        _requested = null;
        _active = null;
        _outgoingArtwork = null;
        _layout = null;
        ReleaseGpu();
    }

    public void Dispose() => Clear();
}
