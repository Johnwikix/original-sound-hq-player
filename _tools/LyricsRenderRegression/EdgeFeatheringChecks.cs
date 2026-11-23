using System.Buffers.Binary;
using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;

internal static class EdgeFeatheringChecks
{
    private const int Width = 680;
    private const int CurrentLine = 12;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        string output = Path.Combine(AppContext.BaseDirectory, "lyrics-edge-feathering");
        Directory.CreateDirectory(output);
        var metrics = new List<string>();
        RunCase(canvas, 900, false, false, output, metrics);
        RunCase(canvas, 400, false, false, null, metrics);
        RunCase(canvas, 900, true, false, null, metrics);
        RunCase(canvas, 900, true, true, null, metrics);
        File.WriteAllLines(Path.Combine(output, "measurements.txt"), metrics);
    }

    private static void RunCase(CanvasAnimatedControl canvas, int height, bool hdr, bool parallax,
        string? output, List<string> metrics)
    {
        var device = canvas.Device;
        var bounds = new Rect(0, 0, Width, height);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = bounds, LyricsViewport = bounds };
        var color = hdr ? PlaybackHdrColor.FromNits(200, 1000) : default;
        var format = hdr ? DirectXPixelFormat.R16G16B16A16Float : DirectXPixelFormat.B8G8R8A8UIntNormalized;
        using var target = new CanvasRenderTarget(device, Width, height, 96, format, CanvasAlphaMode.Premultiplied);

        void SetSettings(bool feathered)
        {
            LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Center, true, 1, 0,
                0, 0, 0, 500, false, false, 0.65, 0.75, 0, EasingType.FlowWave, EaseMode.FlowWave,
                0.42, 60, false, Colors.White, showTranslation: true, showPronunciation: true,
                isLyricsEdgeFeatheringEffectEnabled: feathered, is3DLyricsEnabled: parallax, isAuto3DLyricsEnabled: parallax));
            coordinator.OnUpdate(device, TimeSpan.Zero);
        }

        void Draw()
        {
            using var drawing = target.CreateDrawingSession();
            drawing.Clear(Colors.Transparent);
            if (hdr) drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            using var scope = CanvasDrawingSessionScope.Borrow(drawing);
            coordinator.OnDraw(device, scope.Context, color);
        }

        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            if (parallax)
            {
                typeof(LyricsRenderCoordinator).GetMethod("SetLyricsParallaxPointer", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(coordinator, [new Vector2(0.55f, -0.35f)]);
            }
            const string lines = "Quiet signals;Dawn on the water;Now we follow the stars;Every step closer;The air turns still;Beyond the shoreline";
            string[] text = lines.Split(';');
            var lyrics = new List<LyricLine>();
            for (int i = 0; i < 22; i++)
            {
                int start = i * 1000;
                lyrics.Add(new()
                {
                    StartMs = start,
                    EndMs = start + 1000,
                    Words = [new() { Word = text[i % text.Length], StartMs = start, DurationMs = 1000 }],
                    TransLateText = "遠くに小さな光が見える",
                    PronunciationText = "tooku ni chiisana hikari ga mieru",
                });
            }

            TimeProgressBus.SetClock(() => CurrentLine * 1000 + 700);
            LyricsFontSizeBus.Publish(36);
            UILyricsBus.Publish(lyrics);
            IsPlayingBus.Publish(true);
            SetSettings(true);
            for (int i = 0; i < 100; i++)
                coordinator.OnUpdate(device, TimeSpan.FromSeconds(1d / 60));
            Draw();
            var after = target.GetPixelBytes();
            if (output is not null) SavePreview(target, output, "after.png");

            // Feed the old gradient to the same cached mask and render at the same time and location.
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var mask = (EdgeFadeMaskRenderer)typeof(LyricsRenderCoordinator).GetField("_edgeFadeMask", flags)!.GetValue(coordinator)!;
            var actualBounds = (Rect)typeof(LyricsRenderCoordinator).GetField("_edgeFadeBounds", flags)!.GetValue(coordinator)!;
            float oldStop = Math.Min(0.5f, 16f / (float)actualBounds.Height);
            mask.Update(device, actualBounds, [
                new() { Color = Colors.Transparent },
                new() { Position = oldStop, Color = Colors.White },
                new() { Position = 1 - oldStop, Color = Colors.White },
                new() { Position = 1, Color = Colors.Transparent }
            ]);
            Draw();
            var before = target.GetPixelBytes();
            if (output is not null) SavePreview(target, output, "before.png");

            SetSettings(false);
            Draw();
            var disabled = target.GetPixelBytes();
            var label = $"{height} DIP {(hdr ? "HDR" : "SDR")} {(parallax ? "tilted" : "flat")}";
            var step = hdr ? 8 : 4;
            float Alpha(byte[] frame, int index) => hdr
                ? (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(index + 6, 2)))
                : frame[index + 3] / 255f;
            double Sum(byte[] frame, int top, int bottom)
            {
                double result = 0;
                for (int i = top * Width * step; i < bottom * Width * step; i += step)
                    result += Alpha(frame, i);
                return result;
            }

            int band = (int)(height * 0.12);
            double topBefore = Sum(before, 0, band), topAfter = Sum(after, 0, band);
            double bottomBefore = Sum(before, height - band, height), bottomAfter = Sum(after, height - band, height);
            Check(topBefore > 50 && topAfter > 0 && topAfter < topBefore * 0.83, label + " fades the top glyphs");
            Check(bottomBefore > 50 && bottomAfter > 0 && bottomAfter < bottomBefore * 0.83, label + " fades the bottom glyphs");
            int centerStart = (height * 35 / 100) * Width * step;
            int centerEnd = (height * 65 / 100) * Width * step;
            Check(Sum(before, height * 35 / 100, height * 65 / 100) > 100 &&
                after.AsSpan(centerStart, centerEnd - centerStart).SequenceEqual(before.AsSpan(centerStart, centerEnd - centerStart)),
                label + " keeps central glyph pixels identical");
            Check(Sum(disabled, 0, band) >= topBefore && Sum(disabled, height - band, height) >= bottomBefore,
                label + " leaves unfeathered mode unattenuated");
            metrics.Add(FormattableString.Invariant(
                $"{label}: top alpha {topBefore:F1} → {topAfter:F1}; bottom alpha {bottomBefore:F1} → {bottomAfter:F1}; central pixels identical; off setting passes."));
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static void SavePreview(CanvasRenderTarget lyrics, string output, string file)
    {
        using var composite = new CanvasRenderTarget(lyrics.Device, lyrics.SizeInPixels.Width, lyrics.SizeInPixels.Height, 96);
        using (var drawing = composite.CreateDrawingSession())
        using (var background = new CanvasLinearGradientBrush(lyrics.Device,
            Windows.UI.Color.FromArgb(255, 28, 35, 63), Windows.UI.Color.FromArgb(255, 74, 40, 61)))
        {
            background.EndPoint = new Vector2(Width, lyrics.SizeInPixels.Height);
            drawing.FillRectangle(0, 0, Width, lyrics.SizeInPixels.Height, background);
            drawing.DrawImage(lyrics);
        }
        composite.SaveAsync(Path.Combine(output, file), CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}