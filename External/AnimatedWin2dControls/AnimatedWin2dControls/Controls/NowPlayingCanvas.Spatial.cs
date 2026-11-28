using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using System;
using System.Numerics;
using Windows.Foundation;

namespace AnimatedWin2dControls.Controls;

public sealed partial class NowPlayingCanvas
{
    // Foreground drawing is serialized by the scene cache gate. The title effect is
    // separate because title drawing and its presentation handoff are UI-thread work.
    private Transform3DEffect? _foregroundProjectionEffect;
    private Transform3DEffect? _hdrTitleProjectionEffect;

    /// <summary>Applies a viewport-local playback projection. Pass null to restore standalone lyric projection.</summary>
    public void SetPlaybackSpatialProjection(Matrix4x4? projection)
        => _coordinator.SetPlaybackSpatialProjection(projection);

    private bool DrawProjectedCover(PlaybackCanvasFrame frame, CanvasDrawContext destination, Matrix4x4 projection)
    {
        if (_sceneCover is not { } cover || (!frame.Hdr.IsEnabled && _background is ImageBackgroundRenderer))
            return false;

        if (projection.IsIdentity)
            return cover.Draw(frame, destination);

        using var commands = new CanvasCommandList(frame.Device);
        bool drewCover;
        using (var drawing = commands.CreateDrawingSession())
        {
            // Recording can introduce its own rasterization at non-native DPI.
            if (frame.Hdr.IsEnabled)
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            using var source = CanvasDrawingSessionScope.Borrow(drawing);
            drewCover = cover.Draw(frame, source.Context);
        }

        if (drewCover)
            DrawProjectedImage(destination, commands, projection, frame.Hdr.IsEnabled, ref _foregroundProjectionEffect);

        return drewCover;
    }

    private void DrawProjectedHdrTitle(AnimatedTextBlock.AnimatedTextBlock? title, CanvasDrawingSession destination,
        PlaybackCanvasFrame frame, Rect bounds, TimeSpan elapsed, Matrix4x4 projection)
    {
        if (title is null) return;
        if (projection.IsIdentity)
        {
            title.DrawInScene(destination, frame, bounds, elapsed);
            return;
        }

        using var commands = new CanvasCommandList(frame.Device);
        bool drewTitle;
        using (var drawing = commands.CreateDrawingSession())
        {
            drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            drewTitle = title.DrawInScene(drawing, frame, bounds, elapsed);
        }

        if (!drewTitle) return;
        using var scope = CanvasDrawingSessionScope.Borrow(destination);
        DrawProjectedImage(scope.Context, commands, projection, hdr: true, ref _hdrTitleProjectionEffect);
    }

    private static void DrawProjectedImage(CanvasDrawContext destination, CanvasCommandList commands,
        Matrix4x4 projection, bool hdr, ref Transform3DEffect? cachedEffect)
    {
        var effect = cachedEffect ??= new Transform3DEffect();
        effect.TransformMatrix = projection;
        effect.BufferPrecision = hdr ? CanvasBufferPrecision.Precision16Float : null;
        effect.Source = commands;
        try
        {
            destination.DrawImage(effect);
        }
        finally
        {
            effect.Source = null;
        }
    }

    private void ReleaseForegroundProjection()
    {
        _foregroundProjectionEffect?.Dispose();
        _foregroundProjectionEffect = null;
    }

    private void ReleaseHdrTitleProjection()
    {
        _hdrTitleProjectionEffect?.Dispose();
        _hdrTitleProjectionEffect = null;
    }
}
