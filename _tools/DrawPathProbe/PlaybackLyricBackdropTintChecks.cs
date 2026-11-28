using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Enums;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using AnimatedWin2dControls.Renderer;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;

internal sealed partial class ProbeApplication
{
    private object CheckLyricBackdropTint()
    {
        const int width = 640, height = 400;
        var device = _canvas.Device;
        var dark = new Vector3(.12f, .31f, .07f);
        var alternate = new Vector3(.34f, .09f, .40f);
        var bright = new Vector3(4.5f, 3.8f, 3.2f);
        var brightSdr = new Vector3(.99f, .97f, .93f);
        var hdr = PlaybackHdrColor.FromNits(200, 600);
        var region = new Rect(58, 28, 510, 344);
        var backgroundField = typeof(NowPlayingCanvas).GetField("_background", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var coordinatorField = typeof(NowPlayingCanvas).GetField("_coordinator", BindingFlags.Instance | BindingFlags.NonPublic)!;
        using var scene = new NowPlayingCanvas { LyricsRegion = region };
        using var background = new TintProbeBackground(device, new Rect(0, 0, width, height), dark);
        backgroundField.SetValue(scene, background);
        var coordinator = (LyricsRenderCoordinator)coordinatorField.GetValue(scene)!;
        coordinator.Canvas = _canvas;
        coordinator.LyricsRegion = region;
        coordinator.LyricsViewport = new Rect(0, 0, width, height);
        coordinator.Attach();
        coordinator.OnCreateResources();
        using var sdrTarget = new CanvasRenderTarget(device, width, height, 96,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
        using var hdrTarget = CreateFloatTarget(device, width, height);
        using var foreground = CreateFloatTarget(device, width, height);

        long time = 760;
        TimeProgressBus.SetClock(() => time);
        IsPlayingBus.Publish(true);
        LyricsFontSizeBus.Publish(52);
        try
        {
            var lineFade = new[]
            {
                CheckLineFade(sdrTarget, default, WordByWordEffectMode.Never),
                CheckLineFade(sdrTarget, default, WordByWordEffectMode.Auto),
                CheckLineFade(hdrTarget, hdr, WordByWordEffectMode.Never),
                CheckLineFade(hdrTarget, hdr, WordByWordEffectMode.Auto),
                CheckLineFade(sdrTarget, default, WordByWordEffectMode.Never, waitingOpacity: 1),
                CheckLineFade(hdrTarget, hdr, WordByWordEffectMode.Never, waitingOpacity: 1)
            };

            time = 760;
            UILyricsBus.Publish([new LyricLine
            {
                StartMs = 0, EndMs = 2100, HighlightEndMs = 2100,
                Words =
                {
                    new() { Word = "MMMM ", StartMs = 0, DurationMs = 1000 },
                    new() { Word = "MMMM", StartMs = 1000, DurationMs = 1000 }
                }
            }]);
            SetSettings();
            Step();
            byte[] plain = RenderForeground();
            int middle = FindInkMidpoint(plain);
            int split = FindInkSplit(plain);
            background.SetSplit(dark, alternate, split);
            var sdr = CheckSweep(sdrTarget, default, plain, middle, split);
            background.SetSplit(dark * hdr.White, alternate * hdr.White, split);
            var hdrDark = CheckSweep(hdrTarget, hdr, plain, middle, split);
            var waiting = CheckWaitingBlend(middle);

            background.SetColor(brightSdr);
            var sdrBright = CheckGlyphFootprint(sdrTarget, default, plain);
            background.SetColor(Vector3.One);
            var sdrPaperWhite = CheckGlyphFootprint(sdrTarget, default, plain);
            background.SetColor(bright);
            var hdrBright = CheckGlyphFootprint(hdrTarget, hdr, plain);
            background.SetColor(new Vector3(hdr.White));
            var hdrPaperWhite = CheckGlyphFootprint(hdrTarget, hdr, plain);
            var at144Dpi = Check144Dpi();

            background.SetColor(dark);
            var sdrHover = CheckHover(sdrTarget, default, plain, middle);
            var hdrHover = CheckHover(hdrTarget, hdr, plain, middle);
            SetSettings(custom: true);
            Step();
            var custom = Capture(sdrTarget, default);
            var customHdr = Capture(hdrTarget, hdr);
            SetSettings();
            Step();
            UseImageBackdrop(scene) = true;
            var image = Capture(sdrTarget, default);
            var imageHdr = Capture(hdrTarget, hdr);
            if (!custom.AsSpan().SequenceEqual(image) || !customHdr.AsSpan().SequenceEqual(imageHdr))
                throw new InvalidOperationException("Image mode and a custom white lyric did not fall back to the same ordinary blend.");
            UseImageBackdrop(scene) = false;

            SetSettings(projected: true);
            scene.SetLyricsParallaxPointer(new Vector2(.62f, -.48f));
            Step();
            byte[] projected = RenderForeground();
            if (projected.AsSpan().SequenceEqual(plain))
                throw new InvalidOperationException("The fixture did not activate the projected lyric path.");
            var coverage = CheckCoverage(projected, Capture(hdrTarget, hdr));
            Measure("hdr-tint", () => Draw(hdrTarget, hdr), iterations: 120, warmup: 30);
            Measure("sdr-tint", () => Draw(sdrTarget, default), iterations: 120, warmup: 30);
            SetSettings(custom: true, projected: true);
            Step();
            Measure("sdr-custom-white", () => Draw(sdrTarget, default), iterations: 120, warmup: 30);
            Measure("hdr-custom-white", () => Draw(hdrTarget, hdr), iterations: 120, warmup: 30);

            return new
            {
                Sdr = sdr, Hdr = hdrDark, SdrBright = sdrBright, SdrPaperWhite = sdrPaperWhite,
                HdrBright = hdrBright, HdrPaperWhite = hdrPaperWhite,
                At144Dpi = at144Dpi, WaitingBlend = waiting, LineFade = lineFade, SdrHover = sdrHover, HdrHover = hdrHover,
                ProjectedCoverage = coverage, Size = new { Width = width, Height = height, Dpi = 96 }
            };
        }
        finally
        {
            TimeProgressBus.SetClock(null);
            coordinator.PrepareForShutdown();
        }

        void Step()
        {
            for (int i = 0; i < 90; i++) coordinator.OnUpdate(device, Delta);
        }

        static void SetSettings(bool custom = false, bool projected = false, double waitingOpacity = .5,
            WordByWordEffectMode wordMode = WordByWordEffectMode.Auto)
            => LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, true,
                1, 0, 0, 0, 0, 100, false, false, waitingOpacity, .7, 0, EasingType.Sine, EaseMode.Out,
                .35, 60, custom, Colors.White, isLyricsEdgeFeatheringEffectEnabled: projected,
                is3DLyricsEnabled: projected, isAuto3DLyricsEnabled: projected, wordByWordEffectMode: wordMode));

        void Draw(CanvasRenderTarget target, PlaybackHdrColor range)
        {
            using var session = target.CreateDrawingSession();
            session.Clear(Colors.Transparent);
            if (range.IsEnabled)
                session.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            using var scope = CanvasDrawingSessionScope.Borrow(session);
            DrawPlaybackScene(scene, new PlaybackCanvasFrame(device, target.Size, target.Dpi, range), scope.Context);
        }

        byte[] Capture(CanvasRenderTarget target, PlaybackHdrColor range)
        {
            // The first shot after a color change can reuse the shader's half-frame cache.
            Draw(target, range);
            Draw(target, range);
            return target.GetPixelBytes();
        }

        byte[] RenderForeground(CanvasRenderTarget? target = null)
        {
            target ??= foreground;
            using (var session = target.CreateDrawingSession())
            {
                session.Clear(Colors.Transparent);
                session.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                using var scope = CanvasDrawingSessionScope.Borrow(session);
                coordinator.OnDraw(device, scope.Context, hdr, consumePendingLyrics: false);
            }
            return target.GetPixelBytes();
        }

        object CheckLineFade(CanvasRenderTarget target, PlaybackHdrColor range, WordByWordEffectMode wordMode,
            double waitingOpacity = .5)
        {
            const int lineStart = 1000, frameMs = 16;
            background.SetColor(range.IsEnabled ? dark * range.White : dark);
            SetSettings(waitingOpacity: waitingOpacity, wordMode: wordMode);
            time = lineStart - frameMs;
            UILyricsBus.Publish([
                new LyricLine
                {
                    StartMs = 0, EndMs = lineStart, HighlightEndMs = lineStart,
                    Words =
                    {
                        new() { Word = "MMM ", StartMs = 0, DurationMs = 450 },
                        new() { Word = "MMM", StartMs = 450, DurationMs = 450 }
                    }
                },
                new LyricLine
                {
                    StartMs = lineStart, EndMs = 4000, HighlightEndMs = 4000,
                    Words =
                    {
                        new() { Word = "MMM ", StartMs = lineStart, DurationMs = 80 },
                        new() { Word = "MMM", StartMs = lineStart + 80, DurationMs = 1500 }
                    }
                }
            ]);
            Step();
            int[] frames = [0, 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 24, 40];
            float white = range.IsEnabled ? range.White : 1f;
            float[] outgoing = new float[frames.Length], incoming = new float[frames.Length];
            int frame = 0;
            for (int i = 0; i < frames.Length; i++)
            {
                while (frame < frames[i])
                {
                    frame++;
                    time += frameMs;
                    coordinator.OnUpdate(device, TimeSpan.FromMilliseconds(frameMs));
                }
                var pixels = Capture(target, range);
                var rows = ReadRows(pixels);
                if (rows.Count != 2)
                    throw new InvalidOperationException($"Expected two separated lyric rows at {time} ms, found {rows.Count}.");
                outgoing[i] = rows[0];
                incoming[i] = rows[1];
            }

            // Peak interior light follows the row while it scrolls. The first timed
            // word is fully swept by the 80 ms sample, so the rest of its rise is row opacity.
            int incomingStart = wordMode == WordByWordEffectMode.Auto ? Array.IndexOf(frames, 6) : 1;
            bool hasSeamJump = false;
            for (int i = 1; i <= 3; i++)
            {
                float exitStep = outgoing[i] - outgoing[i - 1];
                float entryStep = incoming[i] - incoming[i - 1];
                hasSeamJump |= exitStep > .012f || exitStep < -.14f || entryStep < -.012f || entryStep > .14f;
            }
            bool validFade;
            if (waitingOpacity == 1)
            {
                validFade = outgoing.Max() - outgoing.Min() <= .012f && incoming.Max() - incoming.Min() <= .012f;
            }
            else
            {
                bool changedDirection = outgoing[0] > outgoing[^1] && incoming[^1] > incoming[0];
                bool endsMatch = MathF.Abs(outgoing[0] - incoming[^1]) <= .035f &&
                    MathF.Abs(incoming[0] - outgoing[^1]) <= .035f;
                validFade = changedDirection && endsMatch && FadesBetween(outgoing, 1, .22f) &&
                    FadesBetween(incoming, incomingStart, .22f);
            }
            if (hasSeamJump || !validFade)
                throw new InvalidOperationException($"Lyric line brightness did not fade through the handoff ({target.Format}, {wordMode}): " +
                    $"outgoing [{string.Join(", ", outgoing)}], incoming [{string.Join(", ", incoming)}].");

            return new { Format = target.Format.ToString(), WordMode = wordMode.ToString(),
                WaitingOpacity = waitingOpacity, Milliseconds = frames.Select(n => (n - 1) * frameMs).ToArray(),
                Outgoing = outgoing, Incoming = incoming };

            List<float> ReadRows(byte[] pixels)
            {
                var rows = new List<float>(2);
                float rowPeak = 0f;
                float baseLight = Brightness(Read(pixels, 0, range.IsEnabled));
                for (int y = 0; y < height; y++)
                {
                    float peak = 0f;
                    for (int x = 0; x < width; x++)
                    {
                        float light = Brightness(Read(pixels, y * width + x, range.IsEnabled));
                        peak = MathF.Max(peak, (light - baseLight) / white);
                    }
                    if (peak > .08f)
                    {
                        rowPeak = MathF.Max(rowPeak, peak);
                    }
                    else if (rowPeak > 0f)
                    {
                        rows.Add(rowPeak);
                        rowPeak = 0f;
                    }
                }
                if (rowPeak > 0f) rows.Add(rowPeak);
                return rows;
            }

            static bool FadesBetween(float[] samples, int firstIntermediate, float requiredSpan)
            {
                float low = MathF.Min(samples[0], samples[^1]);
                float high = MathF.Max(samples[0], samples[^1]);
                if (high - low < requiredSpan) return false;
                int intermediate = 0;
                for (int i = firstIntermediate; i < samples.Length - 1; i++)
                {
                    if (samples[i] > low + .025f && samples[i] < high - .025f)
                        intermediate++;
                }
                return intermediate >= 3;
            }
        }

        int FindInkMidpoint(byte[] pixels, int pixelWidth = width)
        {
            int minX = pixelWidth, maxX = 0;
            for (int pixel = 0; pixel < pixels.Length / 8; pixel++)
            {
                if (ReadHalf(pixels, pixel * 8 + 6) < .35f) continue;
                minX = Math.Min(minX, pixel % pixelWidth);
                maxX = Math.Max(maxX, pixel % pixelWidth);
            }
            if (maxX - minX < 90)
                throw new InvalidOperationException("The timed lyric fixture did not render both words.");
            return (minX + maxX) / 2;
        }

        int FindInkSplit(byte[] plain)
        {
            int minY = height, maxY = 0;
            for (int pixel = 0; pixel < plain.Length / 8; pixel++)
            {
                if (ReadHalf(plain, pixel * 8 + 6) < .35f) continue;
                minY = Math.Min(minY, pixel / width);
                maxY = Math.Max(maxY, pixel / width);
            }
            if (maxY - minY < 20)
                throw new InvalidOperationException("The timed lyric fixture did not render enough ink for two local backgrounds.");
            return (minY + maxY) / 2;
        }

        object CheckSweep(CanvasRenderTarget target, PlaybackHdrColor range, byte[] before, int midpoint, int split)
        {
            byte[] early = Capture(target, range);
            byte[] late;
            byte[] after;
            time = 2080;
            Step();
            try
            {
                late = Capture(target, range);
                after = RenderForeground();
            }
            finally
            {
                time = 760;
                Step();
            }

            float white = range.IsEnabled ? range.White : 1f;
            var (upperPlayed, upper) = CheckBand(0, split - 1);
            var (lowerPlayed, lower) = CheckBand(split + 1, height);
            if (upperPlayed.Y <= upperPlayed.Z + .006f * white || lowerPlayed.Z <= lowerPlayed.Y + .006f * white ||
                Vector3.Distance(upperPlayed, lowerPlayed) <= .012f * white)
                throw new InvalidOperationException($"The swept ink did not follow both local backdrop colors ({upperPlayed}, {lowerPlayed}).");

            return new { Upper = upper, Lower = lower };

            (Vector3 Played, object Samples) CheckBand(int top, int bottom)
            {
                Vector3 waiting = default, played = default, edge = default;
                int cores = 0, edges = 0;
                Vector3 backdrop = Read(early, top * width, range.IsEnabled);
                for (int y = top; y < bottom; y++)
                for (int x = midpoint + 8; x < width; x++)
                {
                    int pixel = y * width + x;
                    float beforeAlpha = ReadHalf(before, pixel * 8 + 6);
                    float afterAlpha = ReadHalf(after, pixel * 8 + 6);
                    if (beforeAlpha is >= .48f and <= .52f && afterAlpha >= .99f)
                    {
                        waiting += Read(early, pixel, range.IsEnabled);
                        played += Read(late, pixel, range.IsEnabled);
                        cores++;
                    }
                    else if (beforeAlpha is > .10f and < .32f)
                    {
                        edge += Read(early, pixel, range.IsEnabled);
                        edges++;
                    }
                }

                if (cores < 20 || edges < 12)
                    throw new InvalidOperationException($"Insufficient matching lyric pixels ({cores} cores, {edges} edges).");
                waiting /= cores;
                played /= cores;
                edge /= edges;
                if (!(Brightness(edge) > Brightness(backdrop) + .025f * white &&
                      Brightness(waiting) > Brightness(edge) + .025f * white &&
                      Brightness(played) > Brightness(waiting) + .10f * white &&
                      Brightness(played) > .88f * white && Brightness(played) <= white + .01f * white))
                    throw new InvalidOperationException($"The sweep lost the brightness ordering: backdrop {backdrop}, edge {edge}, waiting {waiting}, played {played}.");

                return (played, new { Backdrop = Rgb(backdrop), Edge = Rgb(edge), Waiting = Rgb(waiting),
                    Played = Rgb(played), Cores = cores, Edges = edges });
            }
        }

        object CheckWaitingBlend(int midpoint)
        {
            var checks = new List<object>();
            foreach (double opacity in new[] { .5, .27 })
            {
                SetSettings(waitingOpacity: opacity);
                Step();
                var ink = RenderForeground();
                checks.Add(CheckBackdrop(sdrTarget, default, dark, opacity, ink));
                checks.Add(CheckBackdrop(sdrTarget, default, brightSdr, opacity, ink));
                checks.Add(CheckBackdrop(hdrTarget, hdr, dark * hdr.White, opacity, ink));
                checks.Add(CheckBackdrop(hdrTarget, hdr, bright, opacity, ink));
            }
            SetSettings();
            Step();
            return checks;

            object CheckBackdrop(CanvasRenderTarget target, PlaybackHdrColor range, Vector3 backdrop, double opacity, byte[] ink)
            {
                background.SetColor(backdrop);
                var tinted = Capture(target, range);
                SetSettings(custom: true, waitingOpacity: opacity);
                Step();
                var ordinary = Capture(target, range);
                SetSettings(waitingOpacity: opacity);
                Step();

                int compared = 0;
                float maxDifference = 0f;
                for (int y = 0; y < height; y++)
                for (int x = midpoint + 8; x < width; x++)
                {
                    int pixel = y * width + x;
                    if (ReadHalf(ink, pixel * 8 + 6) <= .001f) continue;
                    compared++;
                    float delta = Vector3.Distance(Read(tinted, pixel, range.IsEnabled), Read(ordinary, pixel, range.IsEnabled));
                    maxDifference = MathF.Max(maxDifference, delta);
                }

                float tolerance = range.IsEnabled ? .006f : .008f;
                if (compared < 100 || maxDifference > tolerance)
                    throw new InvalidOperationException($"Waiting glyphs diverged from normal white at opacity {opacity} over {backdrop}: difference {maxDifference:F4}, {compared} pixels.");
                return new { Range = range.IsEnabled ? "HDR" : "SDR", Opacity = opacity, Backdrop = Rgb(backdrop), Compared = compared, MaxDifference = maxDifference };
            }
        }

        object Check144Dpi()
        {
            const float dpi = 144;
            const DirectXPixelFormat format = DirectXPixelFormat.R16G16B16A16Float;
            using var target = new CanvasRenderTarget(device, width, height, dpi, format, CanvasAlphaMode.Premultiplied);
            using var text = new CanvasRenderTarget(device, width, height, dpi, format, CanvasAlphaMode.Premultiplied);
            var plain = RenderForeground(text);
            var paperWhite = CheckGlyphFootprint(target, hdr, plain);
            background.SetColor(dark);
            var aligned = CheckCoverage(plain, Capture(target, hdr));
            return new { PaperWhite = paperWhite, Dpi = dpi, PhysicalWidth = target.SizeInPixels.Width, aligned };
        }

        object CheckGlyphFootprint(CanvasRenderTarget target, PlaybackHdrColor range, byte[] plain)
        {
            bool isHdr = range.IsEnabled;
            var composed = Capture(target, range);
            Vector3 expectedBackdrop = background.Color;
            Vector3 measuredBackdrop = Read(composed, 0, isHdr);
            if (Vector3.Distance(measuredBackdrop, expectedBackdrop) > (isHdr ? .015f : .008f))
                throw new InvalidOperationException($"The lyric composite changed the backdrop: {measuredBackdrop}, expected {expectedBackdrop}.");
            float maxExteriorDifference = 0f, maxAlphaDifference = 0f;
            int compared = 0;
            for (int pixel = 0; pixel < plain.Length / 8; pixel++)
            {
                if (ReadHalf(plain, pixel * 8 + 6) != 0f) continue;
                compared++;
                float delta = Vector3.Distance(measuredBackdrop, Read(composed, pixel, isHdr));
                maxExteriorDifference = MathF.Max(maxExteriorDifference, delta);
                float alpha = isHdr ? ReadHalf(composed, pixel * 8 + 6) : composed[pixel * 4 + 3] / 255f;
                maxAlphaDifference = MathF.Max(maxAlphaDifference, MathF.Abs(alpha - 1f));
            }

            float tolerance = isHdr ? .015f * range.White : .012f;
            if (compared < 1000 || maxExteriorDifference > tolerance || maxAlphaDifference > .002f)
                throw new InvalidOperationException($"The lyric changed fully transparent surroundings at {target.Dpi} DPI: RGB difference {maxExteriorDifference:F4}, alpha difference {maxAlphaDifference:F4}.");
            return new { Backdrop = Rgb(measuredBackdrop), Compared = compared,
                MaxExteriorDifference = maxExteriorDifference, MaxAlphaDifference = maxAlphaDifference };
        }

        object CheckHover(CanvasRenderTarget target, PlaybackHdrColor range, byte[] plain, int midpoint)
        {
            bool isHdr = range.IsEnabled;
            float white = isHdr ? range.White : 1f;
            var hoverIndex = typeof(LyricsRenderCoordinator).GetField("_hoveredLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var normal = Capture(target, range);
            hoverIndex.SetValue(coordinator, 0);
            try
            {
                var hovered = Capture(target, range);
                float outsideGain = 0f, waitingGain = 0f, playedShift = 0f;
                for (int pixel = 0; pixel < width * height; pixel++)
                {
                    float alpha = ReadHalf(plain, pixel * 8 + 6);
                    float gain = Brightness(Read(hovered, pixel, isHdr)) - Brightness(Read(normal, pixel, isHdr));
                    if (alpha == 0f)
                        outsideGain = MathF.Max(outsideGain, gain);
                    else if (pixel % width > midpoint + 8 && alpha is >= .48f and <= .52f)
                        waitingGain = MathF.Max(waitingGain, gain);
                    else if (alpha >= .999f)
                        playedShift = MathF.Max(playedShift, MathF.Abs(gain));
                }
                if (outsideGain < .04f * white || waitingGain < .025f * white || playedShift > .20f * white)
                    throw new InvalidOperationException($"The lyric hover changed the backdrop and lyric unexpectedly ({outsideGain}, {waitingGain}, {playedShift}).");
                return new { OutsideGain = outsideGain, WaitingGain = waitingGain, PlayedShift = playedShift };
            }
            finally
            {
                hoverIndex.SetValue(coordinator, -1);
            }
        }

        static float Brightness(Vector3 color) => Vector3.Dot(color, new Vector3(.2126f, .7152f, .0722f));

        object CheckCoverage(byte[] plain, byte[] projected)
        {
            Vector3 baseColor = Read(projected, 0, true);
            int covered = 0, displaced = 0, leaked = 0;
            for (int pixel = 0; pixel < plain.Length / 8; pixel++)
            {
                float alpha = ReadHalf(plain, pixel * 8 + 6);
                float delta = Vector3.Distance(baseColor, Read(projected, pixel, true));
                if (alpha >= .15f)
                {
                    covered++;
                    if (delta < .04f) displaced++;
                }
                else if (alpha == 0f && delta > .06f)
                    leaked++;
            }
            if (covered < 300 || displaced > covered * .04f || leaked > covered * .04f)
                throw new InvalidOperationException($"The projected backdrop mask did not match the foreground ({covered} covered, {displaced} missing, {leaked} leaked).");
            return new { Covered = covered, Displaced = displaced, Leaked = leaked };
        }

        static Vector3 Read(byte[] pixels, int pixel, bool hdr)
        {
            int i = pixel * (hdr ? 8 : 4);
            return hdr
                ? new Vector3(ReadHalf(pixels, i), ReadHalf(pixels, i + 2), ReadHalf(pixels, i + 4))
                : new Vector3(pixels[i + 2], pixels[i + 1], pixels[i]) / 255f;
        }

        static float[] Rgb(Vector3 color) => [color.X, color.Y, color.Z];
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "DrawScene")]
    private static extern void DrawPlaybackScene(NowPlayingCanvas scene, PlaybackCanvasFrame frame, CanvasDrawContext session);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_useImageBackground")]
    private static extern ref bool UseImageBackdrop(NowPlayingCanvas scene);

    private sealed class TintProbeBackground : BaseBackgroundRenderer
    {
        private readonly CanvasSolidColorBrush _brush;
        private readonly CanvasSolidColorBrush _lowerBrush;
        private readonly CanvasGeometry _surface;
        private readonly CanvasDevice _device;
        private readonly Rect _bounds;
        private CanvasGeometry? _lowerSurface;
        private bool _disposed;

        internal TintProbeBackground(CanvasDevice device, Rect bounds, Vector3 color)
        {
            _device = device;
            _bounds = bounds;
            _brush = new CanvasSolidColorBrush(device, Colors.Black);
            _lowerBrush = new CanvasSolidColorBrush(device, Colors.Black);
            _surface = CanvasGeometry.CreateRectangle(device, bounds);
            SetColor(color);
        }

        internal Vector3 Color { get; private set; }

        internal void SetColor(Vector3 color)
        {
            Color = color;
            _brush.ColorHdr = new Vector4(color, 1);
            _lowerSurface?.Dispose();
            _lowerSurface = null;
        }

        internal void SetSplit(Vector3 top, Vector3 bottom, int split)
        {
            SetColor(top);
            _lowerBrush.ColorHdr = new Vector4(bottom, 1);
            _lowerSurface = CanvasGeometry.CreateRectangle(_device, new Rect(0, split, _bounds.Width, _bounds.Height - split));
        }

        public override void LoadResources() { }
        public override void Update(TimeSpan deltaTime) { }
        public override void Draw(PlaybackCanvasFrame frame, CanvasDrawContext session)
        {
            session.FillGeometry(_surface, _brush);
            if (_lowerSurface is { } lower)
                session.FillGeometry(lower, _lowerBrush);
        }
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _lowerSurface?.Dispose();
            _surface.Dispose();
            _lowerBrush.Dispose();
            _brush.Dispose();
        }
    }
}
