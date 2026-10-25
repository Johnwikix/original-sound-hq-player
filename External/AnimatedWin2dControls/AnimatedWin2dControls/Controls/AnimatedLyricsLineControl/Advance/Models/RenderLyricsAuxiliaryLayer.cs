using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using System;
using System.Numerics;
using Windows.Foundation;
using Windows.UI;

namespace AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;

/// <summary>An independently measured auxiliary track. All native resources belong to the render thread.</summary>
public sealed class RenderLyricsAuxiliaryLayer
{
    public CanvasTextLayout? Layout { get; private set; }
    public CanvasGeometry? Geometry { get; private set; }
    public Vector2 Position { get; set; }
    public ValueTransition<double> Reveal { get; } = new(0,
        EasingHelper.GetInterpolatorByEasingType<double>(EasingType.Sine), 0.2);
    public double Height => (Layout?.LayoutBounds.Height ?? 0) * Reveal.Value;
    public double SlotHeight(double spacing) => Layout is null ? 0 : (Layout.LayoutBounds.Height + spacing) * Reveal.Value;

    private bool _initialized;
    private CanvasCommandList? _fill;
    private CanvasCommandList? _stroke;
    private TintEffect? _tint;
    private CompositeEffect? _composite;
    private CropEffect? _crop;
    private GaussianBlurEffect? _blur;
    private OpacityEffect? _opacity;

    public void SetVisible(bool visible)
    {
        double target = visible ? 1 : 0;
        if (!_initialized)
        {
            Reveal.JumpTo(target);
            _initialized = true;
        }
        else if (Reveal.TargetValue != target) Reveal.Start(target);
    }

    public void Measure(ICanvasResourceCreator creator, string text, CanvasTextFormat format,
        float width, float height, CanvasHorizontalAlignment alignment)
    {
        DisposeLayout();
        if (string.IsNullOrWhiteSpace(text)) return;
        Layout = new CanvasTextLayout(creator, text, format, width, height)
        {
            HorizontalAlignment = alignment,
            Options = CanvasDrawTextOptions.NoPixelSnap,
        };
    }

    public void CreateGeometry()
    {
        Geometry?.Dispose();
        Geometry = Layout is null ? null : CanvasGeometry.CreateText(Layout);
    }

    public void DisposeGeometry()
    {
        Geometry?.Dispose();
        Geometry = null;
    }

    public void Draw(ICanvasResourceCreator creator, CanvasDrawingSession ds, Vector2 destination,
        double opacity, double blur, double strokeWidth, Color color)
    {
        if (Layout is null || Reveal.Value <= 0 || opacity <= 0) return;
        EnsureCache(creator, strokeWidth);
        _tint!.Color = color;
        var bounds = Layout.LayoutBounds;
        var source = new Rect(bounds.X - 10, bounds.Y - 5, bounds.Width + 20, Height + 10);
        _crop!.SourceRectangle = source;
        _blur!.BlurAmount = (float)blur;
        _opacity!.Opacity = (float)(opacity * Reveal.Value);
        var target = source;
        target.X += destination.X;
        target.Y += destination.Y;
        ds.DrawImage(_opacity, target, source);
    }

    private void EnsureCache(ICanvasResourceCreator creator, double strokeWidth)
    {
        if (_fill is not null) return;
        // Each layer is recorded at its own origin: collapsed layers can overlap
        // logically without contaminating the primary glyph mask or another track.
        _fill = new CanvasCommandList(creator);
        using (var ds = _fill.CreateDrawingSession())
            ds.DrawTextLayout(Layout, Vector2.Zero, Microsoft.UI.Colors.White);
        _composite = new CompositeEffect { Sources = { _fill } };
        if (strokeWidth > 0 && Geometry is not null)
        {
            _stroke = new CanvasCommandList(creator);
            using var style = new CanvasStrokeStyle { LineJoin = CanvasLineJoin.Round };
            using (var ds = _stroke.CreateDrawingSession())
                ds.DrawGeometry(Geometry, Vector2.Zero, Microsoft.UI.Colors.White, (float)strokeWidth, style);
            _composite.Sources.Add(_stroke);
        }
        _tint = new TintEffect { Source = _composite, Color = Microsoft.UI.Colors.White };
        _crop = new CropEffect { Source = _tint, BorderMode = EffectBorderMode.Hard };
        _blur = new GaussianBlurEffect { Source = _crop, BorderMode = EffectBorderMode.Soft };
        _opacity = new OpacityEffect { Source = _blur };
    }

    public void DisposeCache()
    {
        _opacity?.Dispose(); _opacity = null;
        _blur?.Dispose(); _blur = null;
        _crop?.Dispose(); _crop = null;
        _tint?.Dispose(); _tint = null;
        _composite?.Dispose(); _composite = null;
        _stroke?.Dispose(); _stroke = null;
        _fill?.Dispose(); _fill = null;
    }

    public void DisposeLayout()
    {
        DisposeCache();
        DisposeGeometry();
        Layout?.Dispose();
        Layout = null;
    }
}
