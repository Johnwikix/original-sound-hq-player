using System.Buffers.Binary;
using System.Numerics;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Messages;
using AnimatedWin2dControls.Renderer;
using AnimatedWin2dControls.Renderer.Background;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Windows.Foundation;
using Windows.Graphics.DirectX;

internal sealed partial class ProbeApplication
{
    private static readonly PlaybackHdrColor PaperWhite = PlaybackHdrColor.FromNits(200, 200);
    private static readonly PlaybackHdrColor Hdr = PlaybackHdrColor.FromNits(200, 600);

    private (List<object> Backgrounds, object Lyrics, List<object> WarmMotion) MeasurePlaybackHdr()
    {
        if (PaperWhite.White != 2.5f || Hdr.Peak != 7.5f ||
            PlaybackHdrColor.FromNits(double.NaN, 600).IsEnabled ||
            PlaybackHdrColor.FromNits(200, 0).IsEnabled ||
            PlaybackHdrColor.FromNits(200, 100).Peak != PaperWhite.White)
            throw new InvalidOperationException("The playback HDR display values are inconsistent.");

        var device = _canvas.Device;
        var backgrounds = new List<object>();
        var warmMotion = new List<object>();
        var (warmPalette, warmArtwork) = CreateWarmArtwork();
        using (var cache = CreateFloatTarget(device, 480, 270))
        using (var output = CreateFloatTarget(device, 480, 270))
        {
            var frame = new PlaybackCanvasFrame(device, cache.Size, cache.Dpi, PaperWhite);
            foreach (var renderer in CreateBackgrounds())
            {
                using (renderer)
                {
                    renderer.LoadResources();
                    renderer.Update(TimeSpan.FromSeconds(0.23));
                    DrawBackgroundProbe(renderer, frame, cache);
                    byte[] paperPixels = cache.GetPixelBytes();
                    float paper = ReadMax(paperPixels).Rgb;
                    DrawBackgroundProbe(renderer, frame with { Hdr = Hdr }, output);
                    var directPixels = output.GetPixelBytes();
                    var direct = ReadMax(directPixels);
                    DrawBackgroundProbe(renderer, frame with { Hdr = Hdr }, cache);
                    using (var session = output.CreateDrawingSession())
                    {
                        session.Clear(Colors.Transparent);
                        session.DrawImage(cache);
                    }
                    var cachedPixels = output.GetPixelBytes();
                    var cached = ReadMax(cachedPixels);
                    if (paper <= 0 || paper > PaperWhite.White + 0.02f
                        || direct.Rgb < paper - 0.015f
                        || direct.Rgb > Hdr.Peak + 0.04f || Math.Abs(direct.Rgb - cached.Rgb) > 0.015f
                        || direct.Alpha != 1 || cached.Alpha != 1)
                        throw new InvalidOperationException($"{renderer.GetType().Name} failed SDR white, peak or cache checks ({paper}, {direct}, {cached}).");
                    var dark = MeasureBackgroundGradient(paperPixels, directPixels, 480);
                    renderer.IsDark = false;
                    renderer.RefreshColors();
                    renderer.Update(TimeSpan.FromSeconds(10));
                    DrawBackgroundProbe(renderer, frame, cache);
                    DrawBackgroundProbe(renderer, frame with { Hdr = Hdr }, output);
                    var light = MeasureBackgroundGradient(cache.GetPixelBytes(), output.GetPixelBytes(), 480);
                    renderer.SetPalette(warmPalette);
                    renderer.SetArtwork(warmArtwork);
                    renderer.Update(TimeSpan.FromSeconds(3));
                    DrawBackgroundProbe(renderer, frame, cache);
                    renderer.Update(TimeSpan.FromSeconds(1));
                    DrawBackgroundProbe(renderer, frame, cache);
                    DrawBackgroundProbe(renderer, frame with { Hdr = Hdr }, output);
                    var warm = MeasureBackgroundGradient(cache.GetPixelBytes(), output.GetPixelBytes(), 480);

                    CheckBackgroundGradient(renderer, dark, "dark");
                    CheckBackgroundGradient(renderer, light, "light");
                    CheckBackgroundGradient(renderer, warm, "warm");
                    backgrounds.Add(new
                    {
                        Shader = renderer.GetType().Name, PaperWhite = paper, WithHeadroom = direct.Rgb, Cached = cached.Rgb,
                        Dark = dark, Light = light, Warm = warm
                    });
                    if (renderer is FluidBackgroundRenderer or LiquidFlowBackgroundRenderer)
                        warmMotion.Add(MeasureWarmMotion(renderer, frame with { Hdr = Hdr }, cache, output));
                }
            }
        }

        object lyrics;
        using (var target = CreateFloatTarget(device, 1100, 700))
        {
            ConfigureLyrics(false);
            TimeProgressBus.Publish(280);
            IsPlayingBus.Publish(true);
            TimeProgressBus.SetClock(static () => 280);
            for (int i = 0; i < 120; i++) _lyrics.OnUpdate(device, Delta);
            DrawLyricsProbe(target, PaperWhite);
            var white = ReadMax(target);
            DrawLyricsProbe(target, default);
            var sdr = ReadMax(target);
            DrawLyricsProbe(target, Hdr);
            var played = ReadMax(target);
            if (Math.Abs(white.Rgb - PaperWhite.White) > 0.015f || Math.Abs(played.Rgb - white.Rgb) > 0.015f ||
                Math.Abs(sdr.Rgb - 1f) > 0.015f || !played.HasSoftAlpha)
                throw new InvalidOperationException($"Lyric paper white, SDR or antialiasing failed ({white}, {played}, baseline {sdr}, has lines: {_lyrics.HasLyrics}).");

            LyricsSettingsBus.Publish(new("Segoe UI", Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left, false,
                1, 0, 0, 0, 0, 1200, true, true, 0.5, 0.7, 1, EasingType.Sine, EaseMode.Out,
                0.35, 60, false, Colors.Black));
            for (int i = 0; i < 120; i++) _lyrics.OnUpdate(device, Delta);
            DrawLyricsProbe(target, Hdr);
            var black = ReadMax(target);
            if (black.Rgb > 0.02f || black.Alpha < 0.5f)
                throw new InvalidOperationException($"Dark text gained luminance or was invisible ({black}).");
            var tint = Windows.UI.Color.FromArgb(255, 236, 145, 85);
            LyricsSettingsBus.Publish(new("Segoe UI", Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left, true,
                1, 0, 0, 0, 0, 1200, true, true, 0.5, 0.7, 0, EasingType.Sine, EaseMode.Out,
                0.35, 60, true, tint));
            for (int i = 0; i < 120; i++) _lyrics.OnUpdate(device, Delta);
            DrawLyricsProbe(target, Hdr);
            var customInk = ReadOpaqueInk(target.GetPixelBytes());
            if (Vector3.Distance(customInk, Hdr.GetRgb(tint)) > 0.015f)
                throw new InvalidOperationException($"Custom lyric color changed at the paper white input ({customInk}).");
            lyrics = new
            {
                PaperWhite = white.Rgb, Played = played.Rgb, Sdr = sdr.Rgb, DarkText = black.Rgb,
                CustomInk = new[] { customInk.X, customInk.Y, customInk.Z }, played.HasSoftAlpha
            };
        }
        var spatial = MeasureLyricSweep(device);
        var haloClipping = MeasureLyricHaloClipping(device);
        var hover = MeasureLyricHoverContrast(device);
        TimeProgressBus.SetClock(null);
        if (Math.Abs(spatial.PlayedCore - Hdr.White) > 0.015f || Math.Abs(spatial.NextWordCore - Hdr.White) > 0.015f
            || Math.Abs(spatial.UnplayedInk - 0.5f * Hdr.White) > 0.03f
            || Math.Abs(spatial.BeforeWordEnd - Hdr.White) > 0.015f || Math.Abs(spatial.AfterWordEnd - Hdr.White) > 0.015f
            || spatial.MaximumCoreLossWithGlow > 0.035f || spatial.MaximumBackgroundLossWithGlow > 0.02f
            || spatial.MaximumGlowOutsideInk < 0.12f || spatial.GlowArea is < 0.001f or > 0.06f
            || Math.Abs(spatial.GlowPeak - 1.7f * Hdr.White) > 0.012f
            || Math.Abs(spatial.TightGlowPeak - TightHeadroom.Peak) > 0.006f
            || Math.Abs(spatial.NoHeadroomPeak - Hdr.White) > 0.015f
            || Math.Abs(spatial.RaisedWhiteInk - 4f) > 0.015f
            || spatial.BlackGlowPeak > 0.015f || spatial.DarkHaloLoss < 0.025f || spatial.DarkHaloLoss >= Hdr.White
            || spatial.DarkHaloArea is <= 0 or > 0.025f || spatial.CustomGlowHueDrift > 0.005f
            || spatial.FadingGlowPeak <= Hdr.White || spatial.FadingGlowPeak >= spatial.GlowPeak
            || Math.Abs(spatial.SettledGlowPeak - Hdr.White) > 0.015f)
            throw new InvalidOperationException($"Failed sustained lyric fill, boundary or glow luminance: {spatial}.");
        return (backgrounds, new { Readback = lyrics, Sweep = spatial, HaloClipping = haloClipping, Hover = hover }, warmMotion);
    }

    private object CheckLyricSwitch()
    {
        using var target = CreateFloatTarget(_canvas.Device, 680, 400);
        _lyrics.LyricsRegion = new Rect(0, 0, 680, 400);
        ConfigureLyrics(false);
        TimeProgressBus.Publish(280);
        IsPlayingBus.Publish(true);
        TimeProgressBus.SetClock(static () => 280);
        try { return MeasureSwitch(); }
        finally { TimeProgressBus.SetClock(null); }

        object MeasureSwitch()
        {
            for (int i = 0; i < 90; i++) _lyrics.OnUpdate(target, Delta);
            float visible = Opacity();
            UILyricsBus.Publish([], animateChange: true);
            _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(75));
            float outgoing = Opacity();
            // A second clear while fading should not restart the outgoing transition.
            UILyricsBus.Publish([], animateChange: true);
            _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(75));
            float empty = Opacity();
            var incomingLyrics = CreateLyrics();
            UILyricsBus.Publish(incomingLyrics, animateChange: true);
            // Zero elapsed advances resource preparation without advancing the fade clock.
            for (int i = 0; i < incomingLyrics.Count + 2; i++) _lyrics.OnUpdate(target, TimeSpan.Zero);
            _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(100));
            float incoming = Opacity();
            UILyricsBus.Publish(incomingLyrics); // A host sync must not replay the entrance.
            _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(100));
            float settled = Opacity();
            bool motion = new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            if (visible <= .1f || empty != 0 || settled <= .1f
                || (motion && (Math.Abs(outgoing / visible - .5f) > .04f
                    || incoming <= 0 || incoming >= settled))
                || (!motion && outgoing != 0))
                throw new InvalidOperationException($"Unexpected lyric switch opacity: {visible}, {outgoing}, {empty}, {incoming}, {settled}.");
            return new { motion, visible, outgoing, empty, incoming, settled };
        }

        float Opacity()
        {
            DrawLyricsProbe(target, Hdr);
            return ReadMax(target).Alpha;
        }
    }

    private void MeasureHdrSubmission()
    {
        using var target = CreateFloatTarget(_canvas.Device, 1100, 700);
        var frame = new PlaybackCanvasFrame(target.Device, target.Size, target.Dpi, Hdr);
        using BaseBackgroundRenderer fluid = new FluidBackgroundRenderer();
        using BaseBackgroundRenderer mesh = new RotatingMeshBackgroundRenderer();
        fluid.LoadResources();
        mesh.LoadResources();
        Measure("hdr-fluid-offscreen-submit", () => DrawFrame(fluid), iterations: 120, warmup: 30);
        Measure("hdr-mesh-offscreen-submit", () => DrawFrame(mesh), iterations: 120, warmup: 30);

        long position = 15400;
        ConfigureLyrics(true);
        IsPlayingBus.Publish(true);
        TimeProgressBus.SetClock(() => position);
        try
        {
            for (int i = 0; i < 45; i++) _lyrics.OnUpdate(target, Delta);
            Measure("hdr-lyrics-update-and-offscreen-submit", () =>
            {
                position += 4;
                _lyrics.OnUpdate(target, Delta);
                using var drawing = target.CreateDrawingSession();
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.Clear(Colors.Transparent);
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                _lyrics.OnDraw(target, scope.Context, Hdr, consumePendingLyrics: false);
            }, iterations: 120, warmup: 30);
        }
        finally { TimeProgressBus.SetClock(null); }

        void DrawFrame(BaseBackgroundRenderer renderer)
        {
            renderer.Update(Delta);
            using var drawing = target.CreateDrawingSession();
            drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
            drawing.Clear(Colors.Transparent);
            using var scope = CanvasDrawingSessionScope.Borrow(drawing);
            renderer.Draw(frame, scope.Context);
        }
    }

    private static IEnumerable<BaseBackgroundRenderer> CreateBackgrounds()
    {
        yield return new FluidBackgroundRenderer();
        yield return new PS3XMBBackgroundRenderer();
        yield return new RotatingMeshBackgroundRenderer();
        yield return new LiquidFlowBackgroundRenderer();
        yield return new GradientFlowBackgroundRenderer();
        yield return new WavyBackgroundRenderer();
        yield return new ChromaticResonanceBackgroundRenderer();
    }

    private static CanvasRenderTarget CreateFloatTarget(CanvasDevice device, int width, int height)
        => new(device, width, height, 96, DirectXPixelFormat.R16G16B16A16Float, CanvasAlphaMode.Premultiplied);

    private static void DrawBackgroundProbe(BaseBackgroundRenderer renderer, PlaybackCanvasFrame frame, CanvasRenderTarget target)
    {
        using var session = CanvasDrawingSessionScope.Create(target);
        session.Context.Clear(Colors.Transparent);
        renderer.Draw(frame, session.Context);
    }

    private void DrawLyricsProbe(CanvasRenderTarget target, PlaybackHdrColor color)
    {
        using var session = CanvasDrawingSessionScope.Create(target);
        session.Context.Clear(Colors.Transparent);
        _lyrics.OnDraw(target, session.Context, color);
    }

    private static (float Rgb, float Alpha, bool HasSoftAlpha) ReadMax(CanvasRenderTarget target)
        => ReadMax(target.GetPixelBytes());

    private static (float Rgb, float Alpha, bool HasSoftAlpha) ReadMax(byte[] pixels)
    {
        // FP16 rounds alpha and its premultiplied RGB independently. One RGB
        // quantization unit can remain when a subpixel HDR alpha rounds to zero.
        const float halfQuantum = 1f / (1 << 24);
        float rgb = 0, alpha = 0;
        bool softAlpha = false;
        for (int i = 0; i < pixels.Length; i += 8)
        {
            float red = ReadHalf(pixels, i), green = ReadHalf(pixels, i + 2), blue = ReadHalf(pixels, i + 4);
            float color = Math.Max(red, Math.Max(green, blue));
            float opacity = ReadHalf(pixels, i + 6);
            if (!float.IsFinite(color) || !float.IsFinite(opacity) || opacity is < 0 or > 1
                || opacity == 0 && (Math.Abs(red) > halfQuantum || Math.Abs(green) > halfQuantum || Math.Abs(blue) > halfQuantum))
                throw new InvalidOperationException($"Invalid premultiplied FP16 pixel at {i / 8}: {color}, {opacity}.");
            rgb = Math.Max(rgb, color);
            alpha = Math.Max(alpha, opacity);
            softAlpha |= opacity is > 0.1f and < 0.9f;
        }
        return (rgb, alpha, softAlpha);
    }

    private static Vector3 ReadOpaqueInk(byte[] pixels)
    {
        Vector3 rgb = Vector3.Zero;
        for (int offset = 0; offset < pixels.Length; offset += 8)
        {
            if (ReadHalf(pixels, offset + 6) != 1f) continue;
            rgb = Vector3.Max(rgb, new Vector3(ReadHalf(pixels, offset), ReadHalf(pixels, offset + 2),
                ReadHalf(pixels, offset + 4)));
        }
        return rgb;
    }

    private static float ReadHalf(byte[] pixels, int offset)
        => (float)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(pixels.AsSpan(offset, 2)));
}
