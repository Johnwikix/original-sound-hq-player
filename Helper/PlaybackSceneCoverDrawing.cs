using System;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Shaders;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace WinUIMusicPlayer.Helper;

/// <summary>Owns the device resources for one album cover while it is drawn in the playback scene.</summary>
internal sealed class PlaybackSceneCoverDrawing(PlaybackSceneCover cover, CanvasDevice device) : IDisposable
{
    internal CanvasDevice Device { get; } = device;
    internal Size ImageSize { get; } = new(cover.Width, cover.Height);
    private readonly DirectXPixelFormat _pixelFormat = cover.Format;
    private readonly float _sourcePeak = cover.PeakNits;
    private readonly CanvasBitmap _bitmap = CanvasBitmap.CreateFromBytes(device, cover.Pixels, cover.Width, cover.Height,
        cover.Format, 96, CanvasAlphaMode.Premultiplied);
    private CanvasGeometry? _clip;
    private readonly Rect _sourceBounds = new(0, 0, cover.Width, cover.Height);
    private Rect _bounds;
    private float _radius;
    private PixelShaderEffect<HdrCoverToneMap>? _effect;
    private PlaybackHdrColor? _mappedColor;

    internal void Draw(CanvasDrawingSession session, Rect bounds, float radius, float whiteNits, float peakNits, float opacity = 1)
    {
        using var scope = CanvasDrawingSessionScope.Borrow(session);
        Draw(scope.Context, bounds, radius, new PlaybackHdrColor(whiteNits / 80f, peakNits / 80f), opacity);
    }

    internal void Draw(CanvasDrawContext session, Rect bounds, float radius, PlaybackHdrColor color, float opacity)
    {
        if (_clip is null || bounds != _bounds || radius != _radius)
        {
            _clip?.Dispose();
            _clip = CanvasGeometry.CreateRoundedRectangle(Device, bounds, radius, radius);
            _bounds = bounds;
            _radius = radius;
        }

        ICanvasImage image = _bitmap;
        bool sourceIsSdr = _pixelFormat == DirectXPixelFormat.B8G8R8A8UIntNormalized;
        // A native HDR cover can specify a peak without a reference white.
        bool outputSdr = color.Peak <= 0;
        if (sourceIsSdr ? color.IsEnabled : outputSdr || _sourcePeak > color.Peak * 80f)
        {
            if (_effect is null)
            {
                _effect = new PixelShaderEffect<HdrCoverToneMap>();
                _effect.Sources[0] = _bitmap;
            }
            if (_mappedColor != color)
            {
                _effect.ConstantBuffer = new HdrCoverToneMap(_sourcePeak / 80f, outputSdr ? 1f : color.Peak,
                    sourceIsSdr ? color.White : 0f, outputSdr: outputSdr ? 1 : 0);
                _mappedColor = color;
            }
            image = _effect;
        }
        using (session.CreateLayer(opacity, _clip))
            session.DrawImage(image, bounds, _sourceBounds);
    }

    public void Dispose()
    {
        try { _clip?.Dispose(); }
        finally
        {
            try { _effect?.Dispose(); }
            finally { _bitmap.Dispose(); }
        }
    }
}
