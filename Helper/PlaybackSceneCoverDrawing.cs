using System;
using AnimatedWin2dControls.Shaders;
using ComputeSharp.D2D1.WinUI;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;

namespace WinUIMusicPlayer.Helper;

/// <summary>Owns the device resources for one album cover while it is drawn in the HDR scene.</summary>
internal sealed class PlaybackSceneCoverDrawing(PlaybackSceneCover cover, CanvasDevice device) : IDisposable
{
    internal CanvasDevice Device { get; } = device;
    private readonly CanvasBitmap _bitmap = CanvasBitmap.CreateFromBytes(device, cover.Pixels, cover.Width, cover.Height,
        cover.Format, 96, CanvasAlphaMode.Premultiplied);
    private CanvasGeometry? _clip;
    private Rect _bounds;
    private float _radius;
    private PixelShaderEffect<HdrCoverToneMap>? _effect;
    private float _mappedPeak, _mappedWhite;

    internal void Draw(CanvasDrawingSession session, Rect bounds, float radius, float whiteNits, float peakNits)
    {
        if (_clip is null || bounds != _bounds || radius != _radius)
        {
            _clip?.Dispose();
            _clip = CanvasGeometry.CreateRoundedRectangle(Device, bounds, radius, radius);
            _bounds = bounds;
            _radius = radius;
        }

        ICanvasImage image = _bitmap;
        float sdrWhite = cover.Format == DirectXPixelFormat.B8G8R8A8UIntNormalized ? whiteNits : 0;
        if (sdrWhite > 0 || cover.PeakNits > peakNits)
        {
            if (_effect is null)
            {
                _effect = new PixelShaderEffect<HdrCoverToneMap>();
                _effect.Sources[0] = _bitmap;
            }
            if (_mappedPeak != peakNits || _mappedWhite != sdrWhite)
            {
                _effect.ConstantBuffer = new HdrCoverToneMap(cover.PeakNits / 80, peakNits / 80, sdrWhite / 80);
                _mappedPeak = peakNits;
                _mappedWhite = sdrWhite;
            }
            image = _effect;
        }
        using (session.CreateLayer(1, _clip))
            session.DrawImage(image, bounds, _bitmap.Bounds);
    }

    public void Dispose()
    {
        _clip?.Dispose();
        _effect?.Dispose();
        _bitmap.Dispose();
    }
}
