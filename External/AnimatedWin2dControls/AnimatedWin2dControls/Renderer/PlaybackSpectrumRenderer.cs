#nullable enable
using System;
using System.Numerics;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.Geometry;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.UI;

namespace AnimatedWin2dControls.Renderer;

internal sealed class PlaybackSpectrumRenderer : IDisposable
{
    private readonly PlaybackSpectrumSignal _signal = new();
    private readonly Vector2[] _points = new Vector2[PlaybackSpectrumOptions.MaximumCount];
    private readonly Vector2[] _anchors = new Vector2[PlaybackSpectrumOptions.MaximumCount];
    private readonly Vector2[] _normals = new Vector2[PlaybackSpectrumOptions.MaximumCount];
    private Rect _ring;
    private float _ringRadius;
    private float _ringStep;
    private int _ringCount;
    private CanvasDevice? _device;
    private CanvasSolidColorBrush? _brush;
    private Color _brushColor;
    private PlaybackHdrColor _brushHdr;
    private CanvasRenderTarget? _surface;
    private Size _surfaceSize;
    private Transform3DEffect? _tilt;
    private GaussianBlurEffect? _glow;
    private Vector2 _tiltAngles;
    private float _breathing;

    public void Update(PlaybackSpectrumFrame frame, long nowMs, TimeSpan elapsed, PlaybackSpectrumOptions options)
    {
        _signal.Update(frame, nowMs, elapsed, options);
        float target = options.Breathing ? _signal.Bass * options.BreathingIntensity * .01f : 0;
        float rate = target > _breathing ? .2f : .05f;
        _breathing += (target - _breathing) * rate;
        if (!options.Breathing) _breathing = 0;
        if (!options.Glow && !options.Parallax && _surface is not null) ReleaseEffects();
    }

    public void Draw(PlaybackCanvasFrame canvas, CanvasDrawContext session, PlaybackSpectrumOptions options,
        Rect artwork, float radius, PaletteResult? palette, bool dark, Vector2? pointer)
    {
        if (!_signal.HasEnergy || canvas.Size.Width < 1 || canvas.Size.Height < 1) return;
        if (!ReferenceEquals(_device, canvas.Device))
        {
            ReleaseDeviceResources();
            _device = canvas.Device;
        }

        var color = ResolveColor(options, palette, dark);
        if (color.A == 0) return;
        if (_brush is null || _brushColor != color || _brushHdr != canvas.Hdr)
        {
            _brush ??= new CanvasSolidColorBrush(canvas.Device, color);
            if (canvas.Hdr.IsEnabled) _brush.ColorHdr = canvas.Hdr.GetColor(color);
            else _brush.Color = color;
            _brushColor = color;
            _brushHdr = canvas.Hdr;
        }

        using var builder = new CanvasPathBuilder(canvas.Device);
        float height = (float)canvas.Size.Height;
        float extent = height * .10f * (1 + _breathing);
        Vector2 pivot;
        if (options.Placement == SpectrumPlacement.AroundArtwork)
        {
            AddRing(builder, artwork, radius, extent, options.Style);
            pivot = new((float)(artwork.X + artwork.Width * .5), (float)(artwork.Y + artwork.Height * .5));
        }
        else
        {
            bool bottom = options.Placement == SpectrumPlacement.Bottom;
            AddEdge(builder, (float)canvas.Size.Width, height, bottom, extent, options.Style);
            pivot = new((float)canvas.Size.Width * .5f, bottom ? height : 0);
        }

        using var geometry = CanvasGeometry.CreatePath(builder);
        if (options.Glow || options.Parallax)
            DrawEffects(canvas, session, geometry, options, pivot, pointer);
        else
            session.FillGeometry(geometry, _brush);
    }

    private void AddEdge(CanvasPathBuilder builder, float width, float height, bool bottom, float extent, SpectrumStyle style)
    {
        var levels = _signal.Levels;
        float edge = bottom ? height : 0;
        float direction = bottom ? -1f : 1f;
        if (style == SpectrumStyle.Bars)
        {
            float step = width / levels.Length;
            float inset = step >= 3 ? 1f : 0f;
            for (int i = 0; i < levels.Length; i++)
            {
                if (levels[i] <= 0) continue;
                float x = i * step + inset;
                float end = (i + 1) * step - inset;
                float y = edge + direction * Math.Min(height, levels[i] * extent);
                AddQuad(builder, new(x, edge), new(end, edge), new(end, y), new(x, y));
            }
            return;
        }

        int count = Math.Max(2, levels.Length);
        for (int i = 0; i < count; i++)
            _points[i] = new(i * width / (count - 1),
                edge + direction * Math.Min(height, levels[Math.Min(i, levels.Length - 1)] * extent));

        builder.BeginFigure(_points[0]);
        AddCurve(builder, _points.AsSpan(0, count), closed: false);
        builder.AddLine(new Vector2(width, edge));
        builder.AddLine(new Vector2(0, edge));
        builder.EndFigure(CanvasFigureLoop.Closed);
    }

    private void AddRing(CanvasPathBuilder builder, Rect artwork, float radius, float extent, SpectrumStyle style)
    {
        var levels = _signal.Levels;
        const float gap = 2;
        var ring = new Rect(artwork.X - gap, artwork.Y - gap, artwork.Width + 2 * gap, artwork.Height + 2 * gap);
        radius += gap;
        int count = style == SpectrumStyle.Curve ? Math.Max(64, levels.Length) : levels.Length;
        UpdateRing(ring, radius, count);
        if (style == SpectrumStyle.Bars)
        {
            float halfWidth = Math.Clamp(_ringStep * .36f, .4f, 16);
            for (int i = 0; i < count; i++)
            {
                if (levels[i] <= 0) continue;
                var point = _anchors[i];
                var normal = _normals[i];
                var tangent = new Vector2(-normal.Y, normal.X) * halfWidth;
                var tip = point + normal * (levels[i] * extent);
                AddQuad(builder, point - tangent, point + tangent, tip + tangent, tip - tangent);
            }
            return;
        }

        builder.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
        for (int i = 0; i < count; i++)
        {
            float position = i * levels.Length / (float)count;
            int first = (int)position;
            float level = float.Lerp(levels[first], levels[(first + 1) % levels.Length], position - first);
            _points[i] = _anchors[i] + _normals[i] * (level * extent);
        }

        builder.BeginFigure(_points[0]);
        AddCurve(builder, _points.AsSpan(0, count), closed: true);
        builder.EndFigure(CanvasFigureLoop.Closed);
        using var inner = CanvasGeometry.CreateRoundedRectangle(_device!,
            (float)ring.X, (float)ring.Y, (float)ring.Width, (float)ring.Height, radius, radius);
        builder.AddGeometry(inner);
    }

    private void UpdateRing(Rect bounds, float radius, int count)
    {
        if (_ring == bounds && _ringRadius == radius && _ringCount == count) return;
        _ring = bounds;
        _ringRadius = radius;
        _ringCount = count;
        float width = (float)bounds.Width - 2 * radius;
        float height = (float)bounds.Height - 2 * radius;
        float arc = radius * MathF.PI * .5f;
        _ringStep = (2 * (width + height) + 4 * arc) / count;
        for (int i = 0; i < count; i++)
        {
            float distance = (i + .5f) * _ringStep;
            for (int side = 0; side < 4; side++)
            {
                float length = side % 2 == 0 ? width : height;
                var end = side switch
                {
                    0 => new Vector2((float)bounds.Right - radius, (float)bounds.Top + radius),
                    1 => new Vector2((float)bounds.Right - radius, (float)bounds.Bottom - radius),
                    2 => new Vector2((float)bounds.Left + radius, (float)bounds.Bottom - radius),
                    _ => new Vector2((float)bounds.Left + radius, (float)bounds.Top + radius)
                };
                if (distance <= length)
                {
                    var normal = side switch { 0 => -Vector2.UnitY, 1 => Vector2.UnitX, 2 => Vector2.UnitY, _ => -Vector2.UnitX };
                    var tangent = new Vector2(-normal.Y, normal.X);
                    _normals[i] = normal;
                    _anchors[i] = end + normal * radius - tangent * (length - distance);
                    break;
                }
                if (distance <= length + arc || side == 3)
                {
                    float angle = (side - 1) * MathF.PI * .5f + (distance - length) / radius;
                    var (sin, cos) = MathF.SinCos(angle);
                    _normals[i] = new(cos, sin);
                    _anchors[i] = end + _normals[i] * radius;
                    break;
                }
                distance -= length + arc;
            }
        }
    }

    private static void AddQuad(CanvasPathBuilder builder, Vector2 a, Vector2 b, Vector2 c, Vector2 d)
    {
        builder.BeginFigure(a);
        builder.AddLine(b);
        builder.AddLine(c);
        builder.AddLine(d);
        builder.EndFigure(CanvasFigureLoop.Closed);
    }

    private static void AddCurve(CanvasPathBuilder builder, ReadOnlySpan<Vector2> points, bool closed)
    {
        int count = points.Length;
        for (int i = 0; i < (closed ? count : count - 1); i++)
        {
            var first = points[i];
            var next = points[(i + 1) % count];
            var before = points[closed ? (i + count - 1) % count : Math.Max(0, i - 1)];
            var after = points[closed ? (i + 2) % count : Math.Min(count - 1, i + 2)];
            builder.AddCubicBezier(first + (next - before) / 6, next - (after - first) / 6, next);
        }
    }

    private void DrawEffects(PlaybackCanvasFrame frame, CanvasDrawContext session, CanvasGeometry geometry,
        PlaybackSpectrumOptions options, Vector2 pivot, Vector2? pointer)
    {
        var format = frame.Hdr.IsEnabled ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        // 辉光与视差共用一张半分辨率纹理，只在尺寸、DPI、设备或颜色格式变化时重建。
        float dpi = frame.Dpi * .5f;
        if (_surface is null || _surfaceSize != frame.Size || _surface.Dpi != dpi || _surface.Format != format)
        {
            ReleaseEffects();
            _surface = new CanvasRenderTarget(frame.Device, (float)frame.Size.Width, (float)frame.Size.Height,
                dpi, format, CanvasAlphaMode.Premultiplied);
            _surfaceSize = frame.Size;
        }

        using (var drawing = CanvasDrawingSessionScope.Create(_surface))
        {
            var layer = drawing.Context;
            layer.Clear(default);
            layer.FillGeometry(geometry, _brush!);
        }

        ICanvasImage image = _surface;
        if (options.Parallax)
        {
            var size = new Vector2((float)frame.Size.Width, (float)frame.Size.Height);
            var target = pointer is { } position
                ? Vector2.Clamp(position / size * 2 - Vector2.One, -Vector2.One, Vector2.One) * .12f
                : Vector2.Zero;
            _tiltAngles = Vector2.Lerp(_tiltAngles, target, .15f);
            _tilt ??= new Transform3DEffect { Source = _surface };
            var center = new Vector3(pivot, 0);
            var perspective = Matrix4x4.Identity;
            perspective.M34 = -1f / MathF.Max(size.X, size.Y);
            _tilt.TransformMatrix = Matrix4x4.CreateTranslation(-center)
                * Matrix4x4.CreateRotationX(-_tiltAngles.Y) * Matrix4x4.CreateRotationY(_tiltAngles.X)
                * perspective * Matrix4x4.CreateTranslation(center);
            _tilt.BufferPrecision = frame.Hdr.IsEnabled ? CanvasBufferPrecision.Precision16Float : null;
            image = _tilt;
        }
        else _tiltAngles = Vector2.Zero;

        if (options.Glow)
        {
            _glow ??= new GaussianBlurEffect { BlurAmount = 16, BorderMode = EffectBorderMode.Soft };
            if (!ReferenceEquals(_glowSource, image))
            {
                _glow.Source = image;
                _glowSource = image;
                _glow.BufferPrecision = frame.Hdr.IsEnabled ? CanvasBufferPrecision.Precision16Float : null;
            }
            session.DrawImage(_glow);
        }
        session.DrawImage(image);
    }

    private ICanvasImage? _glowSource;

    private static Color ResolveColor(PlaybackSpectrumOptions options, PaletteResult? palette, bool dark)
    {
        if (options.ColorMode == SpectrumColorMode.Custom)
        {
            uint argb = options.CustomArgb;
            return Color.FromArgb((byte)((argb >> 24) * options.Opacity / 100), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }

        var contrast = new Vector3(dark ? 255 : 24);
        var rgb = palette is null ? contrast : Vector3.Lerp(palette.ThemeColor.Color, contrast, .65f);
        if (options.ColorMode == SpectrumColorMode.AdaptiveGray)
            rgb = new Vector3(Vector3.Dot(rgb, new(.2126f, .7152f, .0722f)));
        rgb = Vector3.Clamp(rgb, Vector3.Zero, new(255));
        return Color.FromArgb((byte)(options.Opacity * 255 / 100), (byte)rgb.X, (byte)rgb.Y, (byte)rgb.Z);
    }

    private void ReleaseEffects()
    {
        _glow?.Dispose();
        _glow = null;
        _glowSource = null;
        _tilt?.Dispose();
        _tilt = null;
        _surface?.Dispose();
        _surface = null;
        _surfaceSize = default;
    }

    public void ReleaseDeviceResources()
    {
        ReleaseEffects();
        _brush?.Dispose();
        _brush = null;
        _device = null;
    }

    public void Dispose() => ReleaseDeviceResources();
}
