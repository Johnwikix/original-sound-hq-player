using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using AnimatedWin2dControls.Controls;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
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
        var dark = new Vector3(.14f, .26f, .07f);
        var bright = new Vector3(4.5f, 3.8f, 3.2f);
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

        TimeProgressBus.SetClock(static () => 760);
        IsPlayingBus.Publish(true);
        LyricsFontSizeBus.Publish(52);
        UILyricsBus.Publish([new LyricLine
        {
            StartMs = 0, EndMs = 2100, HighlightEndMs = 2100,
            Words =
            {
                new() { Word = "MMMM ", StartMs = 0, DurationMs = 1000 },
                new() { Word = "MMMM", StartMs = 1000, DurationMs = 1000 }
            }
        }]);

        try
        {
            SetSettings();
            Step();
            byte[] plain = RenderForeground();
            int middle = FindInkMidpoint(plain);
            var sdr = CheckDark(sdrTarget, default, plain, middle);
            var hdrDark = CheckDark(hdrTarget, hdr, plain, middle);

            background.SetColor(bright);
            var high = Capture(hdrTarget, hdr);
            Vector3 outside = Read(high, 0, true);
            if (Vector3.Distance(outside, bright) > .015f || outside.Z <= hdr.White)
                throw new InvalidOperationException($"The HDR composite clipped the bright backdrop outside the glyphs: {outside}.");
            if (Math.Abs(ReadHalf(high, 6) - 1) > .002f)
                throw new InvalidOperationException("The HDR composite changed background coverage.");
            var waitingOutline = CheckBrightOutline(high, plain, middle, played: false, bright.Z);

            background.SetColor(new Vector3(hdr.White));
            var paperWhite = Capture(hdrTarget, hdr);
            var playedOutline = CheckBrightOutline(paperWhite, plain, middle, played: true, hdr.White);
            var at144Dpi = Check144Dpi();

            background.SetColor(dark);
            var hover = CheckHdrHover(plain, middle);
            SetSettings(custom: true);
            Step();
            var custom = Capture(sdrTarget, default);
            SetSettings();
            Step();
            UseImageBackdrop(scene) = true;
            var image = Capture(sdrTarget, default);
            if (!custom.AsSpan().SequenceEqual(image))
                throw new InvalidOperationException("Image mode and a custom white lyric did not fall back to the same ordinary blend.");
            UseImageBackdrop(scene) = false;

            SetSettings(projected: true);
            scene.SetLyricsParallaxPointer(new Vector2(.62f, -.48f));
            Step();
            byte[] projected = RenderForeground();
            if (projected.AsSpan().SequenceEqual(plain))
                throw new InvalidOperationException("The fixture did not activate the projected lyric path.");
            var transformed = CheckDark(hdrTarget, hdr, projected, FindInkMidpoint(projected));
            var coverage = CheckCoverage(projected, Capture(hdrTarget, hdr));
            var mask = BackdropMask(scene) ?? throw new InvalidOperationException("The tint path did not allocate a glyph mask.");
            Measure("hdr-tint", () => Draw(hdrTarget, hdr), iterations: 120, warmup: 30);
            if (!ReferenceEquals(mask, BackdropMask(scene)))
                throw new InvalidOperationException("Stable HDR frames rebuilt the glyph mask.");
            Measure("sdr-tint", () => Draw(sdrTarget, default), iterations: 120, warmup: 30);
            var sdrMask = BackdropMask(scene) ?? throw new InvalidOperationException("The SDR glyph mask is missing.");
            Draw(sdrTarget, default);
            if (!ReferenceEquals(sdrMask, BackdropMask(scene)))
                throw new InvalidOperationException("Stable SDR frames rebuilt the glyph mask.");

            var maskPixels = sdrMask.SizeInPixels;
            var maskFormat = sdrMask.Format.ToString();
            SetSettings(custom: true, projected: true);
            Step();
            Measure("sdr-custom-white", () => Draw(sdrTarget, default), iterations: 120, warmup: 30);
            Measure("hdr-custom-white", () => Draw(hdrTarget, hdr), iterations: 120, warmup: 30);
            if (BackdropMask(scene) is not null || BackdropPatch(scene) is not null)
                throw new InvalidOperationException("The color fallback retained tint buffers.");

            return new
            {
                Sdr = sdr, Hdr = hdrDark, HdrBrightBackdrop = Rgb(outside), PlayedOutline = playedOutline,
                WaitingOutline = waitingOutline, At144Dpi = at144Dpi, Hover = hover, Projected = transformed, Coverage = coverage,
                Size = new { Width = width, Height = height, Dpi = 96 },
                Mask = new { maskPixels.Width, maskPixels.Height, Format = maskFormat }
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

        static void SetSettings(bool custom = false, bool projected = false)
            => LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, true,
                1, 0, 0, 0, 0, 100, false, false, .5, .7, 0, EasingType.Sine, EaseMode.Out,
                .35, 60, custom, Colors.White, isLyricsEdgeFeatheringEffectEnabled: projected,
                is3DLyricsEnabled: projected, isAuto3DLyricsEnabled: projected));

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

        object CheckDark(CanvasRenderTarget target, PlaybackHdrColor range, byte[] plain, int midpoint)
        {
            var drawn = Capture(target, range);
            Vector3 baseColor = Read(drawn, 0, range.IsEnabled);
            Vector3 played = Vector3.Zero, pending = Vector3.Zero, soft = Vector3.Zero;
            int playedCount = 0, pendingCount = 0, softCount = 0;
            for (int pixel = 0; pixel < width * height; pixel++)
            {
                float alpha = ReadHalf(plain, pixel * 8 + 6);
                if (pixel % width < midpoint - 8 && alpha >= .99f)
                {
                    played += Read(drawn, pixel, range.IsEnabled);
                    playedCount++;
                }
                else if (pixel % width > midpoint + 8 && alpha is >= .48f and <= .52f)
                {
                    pending += Read(drawn, pixel, range.IsEnabled);
                    pendingCount++;
                }
                else if (pixel % width > midpoint + 8 && alpha is > .10f and < .32f)
                {
                    soft += Read(drawn, pixel, range.IsEnabled);
                    softCount++;
                }
            }
            if (playedCount < 20 || pendingCount < 20 || softCount < 12)
                throw new InvalidOperationException($"Insufficient lyric pixels (played {playedCount}, pending {pendingCount}, edges {softCount}).");
            played /= playedCount;
            pending /= pendingCount;
            soft /= softCount;
            float white = range.IsEnabled ? range.White : 1f;
            if (Vector3.Distance(played, new Vector3(white)) > .05f)
                throw new InvalidOperationException($"Played lyric deviated from reference white: {played}, expected {white}.");
            var lift = pending - baseColor;
            if (!(pending.Y > pending.X + .018f && pending.X > pending.Z + .015f) ||
                !(lift.Z > .06f && soft.Z > baseColor.Z + .014f && soft.Z < pending.Z - .02f))
                throw new InvalidOperationException($"The lyric did not retain the backdrop color or antialiasing: {baseColor}, {pending}, {soft}.");
            if (!range.IsEnabled && MathF.Max(lift.X, MathF.Max(lift.Y, lift.Z)) - MathF.Min(lift.X, MathF.Min(lift.Y, lift.Z)) > .03f)
                throw new InvalidOperationException($"The SDR foreground did not add the same encoded light to each backdrop channel: {lift}.");
            return new { Background = Rgb(baseColor), Played = Rgb(played), Pending = Rgb(pending), SoftEdge = Rgb(soft) };
        }

        object Check144Dpi()
        {
            const float dpi = 144;
            const DirectXPixelFormat format = DirectXPixelFormat.R16G16B16A16Float;
            using var target = new CanvasRenderTarget(device, width, height, dpi, format, CanvasAlphaMode.Premultiplied);
            using var text = new CanvasRenderTarget(device, width, height, dpi, format, CanvasAlphaMode.Premultiplied);
            var plain = RenderForeground(text);
            var paperWhite = Capture(target, hdr);
            int physicalWidth = checked((int)target.SizeInPixels.Width);
            int middle = FindInkMidpoint(plain, physicalWidth);
            var outline = CheckBrightOutline(paperWhite, plain, middle, played: true, hdr.White, physicalWidth, dpi);
            background.SetColor(dark);
            var aligned = CheckCoverage(plain, Capture(target, hdr));
            return new { PlayedOutline = outline, Dpi = dpi, PhysicalWidth = physicalWidth, aligned };
        }

        object CheckBrightOutline(byte[] composed, byte[] plain, int midpoint, bool played, float backgroundBlue,
            int pixelWidth = width, float dpi = 96)
        {
            const int searchRadius = 4;
            float darkest = backgroundBlue;
            float coreDeviation = 0f;
            int darkEdges = 0, maxDistanceSquared = 0, strongDistanceSquared = 0;
            int pixelHeight = plain.Length / (pixelWidth * 8);
            for (int y = searchRadius; y < pixelHeight - searchRadius; y++)
            for (int x = searchRadius; x < pixelWidth - searchRadius; x++)
            {
                if (played ? x >= midpoint - 8 : x <= midpoint + 8) continue;
                int pixel = y * pixelWidth + x;
                float alpha = ReadHalf(plain, pixel * 8 + 6);
                if (played && alpha >= .999f)
                    coreDeviation = MathF.Max(coreDeviation, MathF.Abs(Read(composed, pixel, true).Z - backgroundBlue));
                if (alpha > .001f) continue;
                float blue = Read(composed, pixel, true).Z;
                if (backgroundBlue - blue <= .08f) continue;

                int nearestSquared = searchRadius * searchRadius + 1;
                for (int dy = -searchRadius; dy <= searchRadius; dy++)
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    int distanceSquared = dx * dx + dy * dy;
                    if (distanceSquared >= nearestSquared) continue;
                    if (ReadHalf(plain, (pixel + dy * pixelWidth + dx) * 8 + 6) >= .1f)
                        nearestSquared = distanceSquared;
                }
                maxDistanceSquared = Math.Max(maxDistanceSquared, nearestSquared);
                if (blue <= backgroundBlue * .5f)
                    strongDistanceSquared = Math.Max(strongDistanceSquared, nearestSquared);
                darkest = MathF.Min(darkest, blue);
                darkEdges++;
            }
            float outset = MathF.Sqrt(maxDistanceSquared);
            float strongOutset = MathF.Sqrt(strongDistanceSquared);
            // At the low contrast threshold, compared against the standalone ink,
            // subpixel glyph coverage and bilinear filtering can add another pixel each.
            if (darkEdges < 15 || darkest > backgroundBlue - .14f || coreDeviation > .04f ||
                outset > 1.25f * dpi / 96f + 2f || strongOutset > 1.25f * dpi / 96f + 1f)
                throw new InvalidOperationException($"HDR {(played ? "played" : "waiting")} outline: {darkest:F3} on {backgroundBlue:F3}, {darkEdges} edge pixels, {outset:F2}px outset, strong edge {strongOutset:F2}px, core deviation {coreDeviation:F3}.");
            return new { Minimum = darkest, Pixels = darkEdges, MaxOutsetPixels = outset, StrongOutsetPixels = strongOutset, CoreMaxDeviation = coreDeviation };
        }

        object CheckHdrHover(byte[] plain, int midpoint)
        {
            var hoverIndex = typeof(LyricsRenderCoordinator).GetField("_hoveredLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var normal = Capture(hdrTarget, hdr);
            hoverIndex.SetValue(coordinator, 0);
            try
            {
                var hovered = Capture(hdrTarget, hdr);
                var patch = BackdropPatch(scene) ?? throw new InvalidOperationException("The HDR hover did not use the backdrop patch.");
                if (patch.Format != DirectXPixelFormat.R16G16B16A16Float)
                    throw new InvalidOperationException("The HDR hover patch lost its floating-point format.");
                float outsideGain = 0f, waitingGain = 0f, playedShift = 0f;
                for (int pixel = 0; pixel < width * height; pixel++)
                {
                    float alpha = ReadHalf(plain, pixel * 8 + 6);
                    float gain = Read(hovered, pixel, true).Z - Read(normal, pixel, true).Z;
                    if (alpha == 0f)
                        outsideGain = MathF.Max(outsideGain, gain);
                    else if (pixel % width > midpoint + 8 && alpha is >= .48f and <= .52f)
                        waitingGain = MathF.Max(waitingGain, gain);
                    else if (alpha >= .999f)
                        playedShift = MathF.Max(playedShift, MathF.Abs(gain));
                }
                if (outsideGain < .15f || waitingGain < .12f || playedShift > .03f)
                    throw new InvalidOperationException($"The HDR tint patch missed the hover ({outsideGain}, {waitingGain}, {playedShift}).");
                return new { OutsideGain = outsideGain, WaitingGain = waitingGain, PlayedShift = playedShift,
                    patch.SizeInPixels.Width, patch.SizeInPixels.Height, Format = patch.Format.ToString() };
            }
            finally
            {
                hoverIndex.SetValue(coordinator, -1);
            }
        }

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

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lyricsBackdropMask")]
    private static extern ref CanvasRenderTarget? BackdropMask(NowPlayingCanvas scene);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_lyricsBackdropPatch")]
    private static extern ref CanvasRenderTarget? BackdropPatch(NowPlayingCanvas scene);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_useImageBackground")]
    private static extern ref bool UseImageBackdrop(NowPlayingCanvas scene);

    private sealed class TintProbeBackground : BaseBackgroundRenderer
    {
        private readonly CanvasSolidColorBrush _brush;
        private readonly CanvasGeometry _surface;
        private bool _disposed;

        internal TintProbeBackground(CanvasDevice device, Rect bounds, Vector3 color)
        {
            _brush = new CanvasSolidColorBrush(device, Colors.Black);
            _surface = CanvasGeometry.CreateRectangle(device, bounds);
            SetColor(color);
        }

        internal void SetColor(Vector3 color) => _brush.ColorHdr = new Vector4(color, 1);
        public override void LoadResources() { }
        public override void Update(TimeSpan deltaTime) { }
        public override void Draw(PlaybackCanvasFrame frame, CanvasDrawContext session) => session.FillGeometry(_surface, _brush);
        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _surface.Dispose();
            _brush.Dispose();
        }
    }
}
