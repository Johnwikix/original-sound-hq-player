using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using AnimatedWin2dControls.Renderer;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;

internal static class HdrProjectionChecks
{
    private const int Width = 1440;
    private const int Height = 900;
    private const float Dpi = 144;
    private const int CurrentLine = 475;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        var device = canvas.Device;
        var size = new Size(Width * 96 / Dpi, Height * 96 / Dpi);
        var coordinator = new LyricsRenderCoordinator
        {
            Canvas = canvas,
            LyricsRegion = new Rect(size.Width / 2, 0, size.Width / 2, size.Height),
            LyricsViewport = new Rect(0, 0, size.Width, size.Height),
        };
        TimeProgressBus.SetClock(() => CurrentLine * 1000 + 500);
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            LyricsFontSizeBus.Publish(45);
            IsPlayingBus.Publish(true);
            const int rowCount = 520;
            var rows = new List<LyricLine>(rowCount);
            for (int i = 0; i < rowCount; i++)
            {
                int start = i * 1000;
                var line = new LyricLine { StartMs = start, EndMs = start + 1000 };
                line.Words.Add(new() { Word = $"Light after midnight row {i}", StartMs = start, DurationMs = 1000 });
                rows.Add(line);
            }
            rows[CurrentLine].TransLateText = "Auxiliary lyric";
            UILyricsBus.Publish(rows);

            var hdr = PlaybackHdrColor.FromNits(200, 1000);
            var playbackFrame = new PlaybackCanvasFrame(device, size, Dpi, hdr);
            var format = DirectXPixelFormat.R16G16B16A16Float;
            using var background = new CanvasRenderTarget(device, (float)size.Width, (float)size.Height, Dpi, format, CanvasAlphaMode.Premultiplied);
            using var target = new CanvasRenderTarget(device, (float)size.Width, (float)size.Height, Dpi, format, CanvasAlphaMode.Premultiplied);
            using var fluid = new FluidBackgroundRenderer { EnableLightWave = true };
            fluid.LoadResources();

            void SetAuto(bool enabled)
            {
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, true, 1, 5,
                    4, 5, 110, 500, false, false, 0.45, 0.6, 0, EasingType.FlowWave, EaseMode.FlowWave,
                    0.35, 60, false, Colors.White, isLyricsEdgeFeatheringEffectEnabled: true,
                    is3DLyricsEnabled: enabled, isAuto3DLyricsEnabled: enabled));
            }

            var elapsed = TimeSpan.FromMilliseconds(16.6667);
            void Draw(int count)
            {
                for (int i = 0; i < count; i++)
                {
                    coordinator.OnUpdate(device, elapsed);
                    if ((i & 1) == 0)
                    {
                        fluid.Update(elapsed + elapsed);
                        using var cache = background.CreateDrawingSession();
                        cache.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                        cache.Clear(Colors.Black);
                        using var scope = CanvasDrawingSessionScope.Borrow(cache);
                        fluid.Draw(playbackFrame, scope.Context);
                    }

                    using var drawing = target.CreateDrawingSession();
                    drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                    drawing.Clear(Colors.Black);
                    using var session = CanvasDrawingSessionScope.Borrow(drawing);
                    session.Context.DrawImage(background);
                    coordinator.OnDraw(device, session.Context, hdr);
                }
            }

            SetAuto(true);
            SetAutomaticPointer(coordinator, null);
            Draw(35);
            var direct = ReadLyricBands(target, background);
            Check(direct[0] > 100 && direct[1] > 100 && direct[2] > 100,
                $"The fixture must show HDR glyphs above and below the current row. Bands {string.Join(", ", direct)}.");

            SetAutomaticPointer(coordinator, new Vector2(-0.003f, 0.003f));
            Draw(1);
            AssertNearCenter(ReadLyricBands(target, background), direct, "first projected frame");
            Draw(25);
            AssertNearCenter(ReadLyricBands(target, background), direct, "settled micro tilt");

            SetAutomaticPointer(coordinator, new Vector2(1, -1));
            Draw(25);
            Check(ReadLyricBands(target, background).Sum() > direct.Sum() * 0.7,
                "Tilting toward an edge must retain the HDR lyric layer over the animated fluid cache.");
            SetAutomaticPointer(coordinator, null);
            Draw(100);
            AssertNearCenter(ReadLyricBands(target, background), direct, "pointer exit");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static int[] ReadLyricBands(CanvasRenderTarget target, CanvasRenderTarget background)
    {
        byte[] pixels = target.GetPixelBytes();
        byte[] backdrop = background.GetPixelBytes();
        var bands = new int[3];
        for (int y = 55; y < Height - 40; y++)
        {
            int band = y < 195 ? 0 : y < 480 ? 1 : 2;
            for (int x = Width / 2; x < Width; x++)
            {
                int offset = (y * Width + x) * 8;
                float red = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(offset, 2)));
                float backgroundRed = (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(backdrop.AsSpan(offset, 2)));
                if (red - backgroundRed > 0.45f) bands[band]++;
            }
        }
        return bands;
    }

    private static void AssertNearCenter(int[] actual, int[] expected, string scenario)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            Check(actual[i] > expected[i] * 0.8,
                $"HDR {scenario} lost glyphs in band {i}. Expected near {expected[i]}, got {actual[i]}. All bands {string.Join(", ", actual)}.");
        }
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetLyricsParallaxPointer")]
    private static extern void SetAutomaticPointer(LyricsRenderCoordinator coordinator, Vector2? position);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
