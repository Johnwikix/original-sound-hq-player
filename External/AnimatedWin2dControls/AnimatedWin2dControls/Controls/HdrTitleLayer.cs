#nullable enable
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.Foundation;
using SceneTitle = AnimatedWin2dControls.Controls.AnimatedTextBlock.AnimatedTextBlock;

namespace AnimatedWin2dControls.Controls;

/// <summary>Owns the title's transparent HDR surface independently of the page swap chain.</summary>
internal sealed class HdrTitleLayer : IDisposable
{
    private readonly UIElement _host;
    private readonly CompositionGraphicsDevice _graphics;
    private readonly SpriteVisual _visual;
    private readonly CompositionSurfaceBrush _brush;
    private CompositionDrawingSurface? _surface;
    private SceneTitle? _title;
    private Rect _bounds;
    private float _dpi, _white;
    private bool _attached;

    internal HdrTitleLayer(UIElement host, PlaybackCanvasFrame frame)
    {
        _host = host;
        var compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(compositor, frame.Device);
        _visual = compositor.CreateSpriteVisual();
        _brush = compositor.CreateSurfaceBrush();
        _brush.Stretch = CompositionStretch.Fill;
        _visual.Brush = _brush;
    }

    internal bool Draw(SceneTitle title, PlaybackCanvasFrame frame, Rect bounds, TimeSpan elapsed)
    {
        if (!title.CanDrawInScene(frame, bounds)) return false;

        var pixels = new Size(Math.Ceiling(bounds.Width * frame.Dpi / 96), Math.Ceiling(bounds.Height * frame.Dpi / 96));
        bool resize = _surface is null || _dpi != frame.Dpi || bounds.Width != _bounds.Width || bounds.Height != _bounds.Height;
        if (resize)
        {
            var surface = _graphics.CreateDrawingSurface(pixels, Microsoft.Graphics.DirectX.DirectXPixelFormat.R16G16B16A16Float,
                Microsoft.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
            _brush.Surface = surface;
            _surface?.Dispose();
            _surface = surface;
        }

        if (bounds != _bounds)
        {
            _visual.Offset = new Vector3((float)bounds.X, (float)bounds.Y, 0);
            _visual.Size = new Vector2((float)bounds.Width, (float)bounds.Height);
        }

        bool redraw = resize || bounds.Width != _bounds.Width || bounds.Height != _bounds.Height ||
            _dpi != frame.Dpi || _white != frame.Hdr.White || !ReferenceEquals(_title, title) || title.NeedsSceneFrame;
        if (redraw)
        {
            using var drawing = CanvasComposition.CreateDrawingSession(_surface!, new Rect(0, 0, pixels.Width, pixels.Height), frame.Dpi);
            drawing.Clear(Colors.Transparent);
            drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            title.DrawInScene(drawing, frame, new Rect(0, 0, bounds.Width, bounds.Height), elapsed);
        }
        _title = title;
        _bounds = bounds;
        _dpi = frame.Dpi;
        _white = frame.Hdr.White;
        return true;
    }

    internal void SetVisible(bool visible)
    {
        if (_attached == visible) return;
        ElementCompositionPreview.SetElementChildVisual(_host, visible ? _visual : null);
        _attached = visible;
    }

    public void Dispose()
    {
        SetVisible(false);
        _visual.Dispose();
        _brush.Dispose();
        _surface?.Dispose();
        _graphics.Dispose();
    }
}
