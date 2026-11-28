using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;

internal sealed partial class ProbeApplication
{
    private static void CheckSpatialForeground(string resultPath)
    {
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            var sdr = CheckSpatialForeground(device, 96, 1, hdr: false);
            var hdr96 = CheckSpatialForeground(device, 96, 1, hdr: true);
            var hdr144 = CheckSpatialForeground(device, 144, 1.3f, hdr: true);
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "PASS", Sdr = sdr, Hdr96 = hdr96, Hdr144 = hdr144 }));
        }
        catch (Exception error)
        {
            Environment.ExitCode = 1;
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "FAIL", Error = error.ToString() }));
        }
    }

    private static object CheckSpatialForeground(CanvasDevice device, float dpi, float scale, bool hdr)
    {
        using var marker = new SpatialMarkerCover(device, hdr);
        using var scene = new NowPlayingCanvas();
        var sceneType = typeof(NowPlayingCanvas);
        const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        sceneType.GetField("_background", privateInstance)!.SetValue(scene, new SpatialMarkerBackground());
        scene.SetSceneCover(marker);
        scene.SpectrumOptions = new PlaybackSpectrumOptions
        {
            Enabled = true,
            Placement = SpectrumPlacement.AroundArtwork,
            Style = SpectrumStyle.Bars,
            ColorMode = SpectrumColorMode.Custom,
            CustomArgb = 0xff00ff00,
            Opacity = 100,
            Glow = false,
        };
        sceneType.GetMethod("SetActiveSpectrumSource", privateInstance)!.Invoke(scene, [new ConstantSpectrumSource()]);
        sceneType.GetMethod("UpdateSpectrum", privateInstance)!.Invoke(scene, [TimeSpan.FromSeconds(1)]);

        const float width = 480;
        const float height = 270;
        float pixelsPerDip = dpi / 96f * scale;
        using var target = new CanvasRenderTarget(device, width * scale, height * scale, dpi,
            DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);
        int pixelWidth = (int)target.SizeInPixels.Width;
        var frame = new PlaybackCanvasFrame(device, new Size(width, height), dpi, hdr ? PaperWhite : default);
        var center = new Vector2(width / 2, height / 2);
        var pose = PlaybackSpatialProjection.CreatePerspective(center, new(13, -19, 14), 800, center);
        var neutral = Draw(Matrix4x4.Identity);
        var tilted = Draw(pose);
        var settled = Draw(Matrix4x4.Identity);

        if (!neutral.AsSpan().SequenceEqual(settled))
            throw new InvalidOperationException("The production foreground did not restore its identity pixels.");
        var cover = Compare(0, "cover");
        var spectrum = Compare(1, "spectrum");
        var background = Measure(2, neutral);
        if (background.Count < 100 || Measure(2, tilted) != background)
            throw new InvalidOperationException($"The background marker moved or disappeared: {background} -> {Measure(2, tilted)}.");
        if (hdr && (cover.Peak < 2.45f || spectrum.Peak < 2.45f))
            throw new InvalidOperationException($"Projected foreground lost HDR paper white. Cover {cover.Peak}, spectrum {spectrum.Peak}.");

        return new
        {
            Cover = new { cover.Count, cover.Movement, cover.Peak },
            Spectrum = new { spectrum.Count, spectrum.Movement, spectrum.Peak },
            Background = new { background.Count, X = background.Center.X, Y = background.Center.Y },
        };

        byte[] Draw(Matrix4x4 projection)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.Transform = Matrix3x2.CreateScale(scale);
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                if (!DrawSpatialScene(scene, frame, scope.Context, projection))
                    throw new InvalidOperationException("The production route did not report a drawn cover.");
            }
            return target.GetPixelBytes();
        }

        (int Count, Vector2 Center, float Peak) Measure(int channel, byte[] pixels)
        {
            int count = 0;
            Vector2 total = default;
            float peak = 0;
            for (int i = 0; i < pixels.Length / 8; i++)
            {
                int offset = i * 8;
                float value = ReadChannel(pixels, offset + channel * 2);
                if (value < .9f || ReadChannel(pixels, offset + (channel + 1) % 3 * 2) > .03f
                    || ReadChannel(pixels, offset + (channel + 2) % 3 * 2) > .03f)
                    continue;
                peak = Math.Max(peak, value);
                total += new Vector2(i % pixelWidth + .5f, i / pixelWidth + .5f) / pixelsPerDip;
                count++;
            }
            return (count, total / Math.Max(1, count), peak);
        }

        (int Count, float Movement, float Peak) Compare(int channel, string label)
        {
            var original = Measure(channel, neutral);
            var projected = Measure(channel, tilted);
            var expected = Vector4.Transform(new Vector4(original.Center, 0, 1), pose);
            var expectedCenter = new Vector2(expected.X, expected.Y) / expected.W;
            float movement = Vector2.Distance(original.Center, projected.Center);
            if (original.Count < 100 || projected.Count < original.Count / 2 || movement < 10
                || Vector2.Distance(expectedCenter, projected.Center) > 5)
                throw new InvalidOperationException($"The {label} did not follow the scene projection: {original} -> {projected}; expected near {expectedCenter}.");
            return (projected.Count, movement, projected.Peak);
        }

        static float ReadChannel(byte[] pixels, int offset)
            => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(pixels, offset));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "DrawPlaybackScene")]
    private static extern bool DrawSpatialScene(NowPlayingCanvas scene, PlaybackCanvasFrame frame,
        CanvasDrawContext context, Matrix4x4? projection);

    private sealed class SpatialMarkerBackground : BaseBackgroundRenderer
    {
        public override void LoadResources() { }
        public override void Update(TimeSpan deltaTime) { }
        public override void Draw(PlaybackCanvasFrame frame, CanvasDrawContext session)
            => session.FillRectangle(new Rect(270, 170, 46, 37), Colors.Blue);
        public override void Dispose() { }
    }

    private sealed class SpatialMarkerCover : IPlaybackSceneCover, IDisposable
    {
        private static readonly Rect Bounds = new(80, 60, 50, 65);
        private readonly CanvasGeometry _geometry;
        private readonly CanvasSolidColorBrush _brush;

        public SpatialMarkerCover(CanvasDevice device, bool hdr)
        {
            _geometry = CanvasGeometry.CreateRectangle(device, Bounds);
            _brush = new CanvasSolidColorBrush(device, Colors.Red);
            if (hdr) _brush.ColorHdr = new Vector4(2.5f, 0, 0, 1);
        }

        public bool Draw(PlaybackCanvasFrame frame, CanvasDrawContext session)
        {
            session.FillGeometry(_geometry, _brush);
            return true;
        }

        public bool TryGetArtworkBounds(out Rect bounds, out float radius)
        {
            bounds = Bounds;
            radius = 0;
            return true;
        }

        public void ReleaseDeviceResources(CanvasDevice? device) { }

        public void Dispose()
        {
            _geometry.Dispose();
            _brush.Dispose();
        }
    }

    private sealed class ConstantSpectrumSource : IPlaybackSpectrumSource
    {
        private readonly float[] _bins = Enumerable.Repeat(.5f, 513).ToArray();
        public void SetActive(bool active) { }
        public bool TryRead(out PlaybackSpectrumFrame frame)
        {
            frame = new(1, 1, Environment.TickCount64, 48000, 1024, _bins, _bins);
            return true;
        }
    }
}
