using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Impressionist;
using AnimatedWin2dControls.Renderer.Background;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Graphics.DirectX;

internal sealed partial class ProbeApplication
{
    private static readonly PlaybackHdrColor TightHeadroom = PlaybackHdrColor.FromNits(200, 206);

    private sealed record BackgroundGradient(float Peak, float GainP95, float NeighborStepP99, float Above105PercentWhite);
    private sealed record LyricSweep(float PlayedCore, float UnplayedInk, float BeforeWordEnd, float AfterWordEnd,
        float NextWordCore, float MaximumCoreLossWithGlow, float MaximumBackgroundLossWithGlow,
        float MaximumGlowOutsideInk, float GlowArea, float GlowPeak, float TightGlowPeak, float NoHeadroomPeak,
        float RaisedWhiteInk, float BlackGlowPeak, float DarkHaloLoss, float DarkHaloArea,
        float CustomGlowHueDrift, float FadingGlowPeak, float SettledGlowPeak, LyricGlowComparison GlowSettings);
    private sealed record GlowSpread(float Core, float CoreLoss, float HaloPeak, float HaloEnergy, int HaloPixels);
    private sealed record LyricGlowComparison(GlowSpread Sdr, GlowSpread Disabled, GlowSpread NoGain,
        GlowSpread Amount05, GlowSpread Amount5, GlowSpread Amount10, GlowSpread Tight5, GlowSpread Tight10,
        float MaximumHdrAlphaShift,
        float MaximumSdrAlphaDelta, float SamePrecisionSdrAlphaDelta, float BrightBackgroundLoss,
        float BrightBackgroundDriftOutsideHalo);

    private static BackgroundGradient MeasureBackgroundGradient(byte[] paper, byte[] high, int width)
    {
        int count = paper.Length / 8;
        var gains = new float[count];
        var steps = new float[count];
        int above = 0;
        float peak = 0;
        for (int i = 0; i < count; i++)
        {
            float value = Luminance(high, i * 8);
            peak = Math.Max(peak, value);
            if (value > PaperWhite.White * 1.05f) above++;
            gains[i] = value - Luminance(paper, i * 8);
            float dx = i % width != 0 ? Math.Abs(gains[i] - gains[i - 1]) : 0;
            float dy = i >= width ? Math.Abs(gains[i] - gains[i - width]) : 0;
            steps[i] = Math.Max(dx, dy);
        }
        Array.Sort(gains);
        Array.Sort(steps);
        return new(peak, gains[(int)(count * 0.95)], steps[(int)(count * 0.99)], (float)above / count);
    }

    private static void CheckBackgroundGradient(BaseBackgroundRenderer renderer, BackgroundGradient gradient, string theme)
    {
        if (gradient.NeighborStepP99 > 0.32f || gradient.Above105PercentWhite > 0.17f
            || gradient.Peak > PaperWhite.White * 1.33f || gradient.GainP95 > 0.40f)
            throw new InvalidOperationException($"{renderer.GetType().Name} {theme} gradient lost smoothness or overexposed: {gradient}.");
    }

    private static object MeasureWarmMotion(BaseBackgroundRenderer renderer, PlaybackCanvasFrame frame,
        CanvasRenderTarget cache, CanvasRenderTarget output)
    {
        const int frames = 12;
        byte[]? previous = null;
        int reused = 0, updated = 0;
        double largestMeanDelta = 0;
        double largestChangedArea = 0;
        for (int step = 0; step < frames; step++)
        {
            renderer.Update(TimeSpan.FromSeconds(1d / 120));
            bool reuse = step % 2 != 0;
            if (!reuse) DrawBackgroundProbe(renderer, frame, cache);
            using (var session = output.CreateDrawingSession())
                session.DrawImage(cache);
            byte[] current = output.GetPixelBytes();
            if (previous is not null)
            {
                bool sameFrame = previous.AsSpan().SequenceEqual(current);
                if (reuse != sameFrame)
                    throw new InvalidOperationException($"{renderer.GetType().Name} stopped advancing or changed a skipped frame.");
                if (reuse)
                {
                    reused++;
                }
                else
                {
                    double totalDelta = 0;
                    int largeChanges = 0;
                    int pixels = current.Length / 8;
                    for (int offset = 0; offset < current.Length; offset += 8)
                    {
                        float delta = Math.Abs(Luminance(current, offset) - Luminance(previous, offset));
                        totalDelta += delta;
                        if (delta > 0.25f) largeChanges++;
                    }
                    largestMeanDelta = Math.Max(largestMeanDelta, totalDelta / pixels);
                    largestChangedArea = Math.Max(largestChangedArea, (double)largeChanges / pixels);
                    updated++;
                }
            }
            previous = current;
        }

        if (largestMeanDelta > 0.09 || largestChangedArea > 0.08)
            throw new InvalidOperationException($"{renderer.GetType().Name} had a broad temporal brightness jump.");
        return new { Shader = renderer.GetType().Name, Frames = frames, Reused = reused, Updated = updated, largestMeanDelta, largestChangedArea };
    }

    private static (PaletteResult Palette, ArtworkPixelData Artwork) CreateWarmArtwork()
    {
        const int edge = ArtworkPixelData.Edge;
        byte[] rgba = new byte[edge * edge * 4], bgra = new byte[rgba.Length];
        var cream = new Vector3(226, 203, 163);
        var tan = new Vector3(182, 137, 92);
        var warmBrown = new Vector3(139, 99, 62);
        var espresso = new Vector3(85, 55, 45);
        for (int y = 0; y < edge; y++)
        {
            float v = y / (edge - 1f);
            for (int x = 0; x < edge; x++)
            {
                float u = x / (edge - 1f);
                Vector3 color = Vector3.Lerp(Vector3.Lerp(cream, tan, u), Vector3.Lerp(warmBrown, espresso, u), v);
                int offset = (y * edge + x) * 4;
                rgba[offset] = bgra[offset + 2] = (byte)color.X;
                rgba[offset + 1] = bgra[offset + 1] = (byte)color.Y;
                rgba[offset + 2] = bgra[offset] = (byte)color.Z;
                rgba[offset + 3] = bgra[offset + 3] = 255;
            }
        }
        return (PaletteExtractor.ExtractFromBgra8Pixels(bgra, edge, edge), new ArtworkPixelData(rgba));
    }

    private LyricSweep MeasureLyricSweep(CanvasDevice device)
    {
        long position = 0;
        TimeProgressBus.SetClock(() => position);
        IsPlayingBus.Publish(true);
        LyricsFontSizeBus.Publish(52);
        UILyricsBus.Publish([new LyricLine
        {
            EndMs = 2100,
            HighlightEndMs = 2100,
            Words =
            [
                new() { Word = "MMMM", StartMs = 0, DurationMs = 1000 },
                new() { Word = "MMMM", StartMs = 1000, DurationMs = 1000 }
            ]
        }]);

        const int width = 1100, height = 700;
        using var target = CreateFloatTarget(device, width, height);
        using var backdrop = new CanvasSolidColorBrush(device, Colors.White) { ColorHdr = new Vector4(5.5f, 5.5f, 5.5f, 1f) };
        SetSettings(0);
        Step(1996);
        byte[] complete = Draw(false);
        Step(700);
        byte[] coveragePixels = Draw(false);
        var inkColumns = Enumerable.Range(0, width)
            .Where(x => Enumerable.Range(0, height).Any(y => ReadHalf(complete, (y * width + x) * 8 + 6) > 0.75f))
            .ToArray();
        if (inkColumns.Length < 80)
            throw new InvalidOperationException("Timed lyric fixture has no fully visible glyphs.");
        int x0 = inkColumns[0], x1 = inkColumns[^1];
        int midpoint = (x0 + x1) / 2;
        int split = Math.Max(1, (x1 - x0) / 8);

        float played = MaxInk(coveragePixels, x0, x0 + split * 2, 0.99f);
        float unplayed = MaxInk(coveragePixels, x1 - split * 2, x1 + 1, 0.25f, normalizeCoverage: false);
        Step(996);
        float before = MaxInk(Draw(false), midpoint - split, midpoint, 0.99f);
        Step(1002);
        float after = MaxInk(Draw(false), midpoint - split, midpoint, 0.99f);
        Step(1680);
        float secondWord = MaxInk(Draw(false), midpoint, midpoint + split * 2, 0.99f);
        Step(768);
        byte[] plainMask = Draw(false);
        byte[] plainOverBackground = Draw(true, Hdr, .6f);
        var raisedWhite = PlaybackHdrColor.FromNits(320, 600);
        float raisedWhiteInk = ReadMax(Draw(true, raisedWhite, 0)).Rgb;
        StartGlow();
        byte[] glowing = Draw(true, Hdr, .6f);
        float maxCoreLoss = 0, maxBackgroundLoss = 0, maxGlow = 0;
        int glowingPixels = 0;
        for (int i = 0; i < glowing.Length; i += 8)
        {
            float alpha = ReadHalf(plainMask, i + 6);
            float delta = Luminance(glowing, i) - Luminance(plainOverBackground, i);
            if (alpha > 0.99f) maxCoreLoss = Math.Max(maxCoreLoss, -delta);
            if (alpha > 0.001f) continue;
            maxBackgroundLoss = Math.Max(maxBackgroundLoss, -delta);
            maxGlow = Math.Max(maxGlow, delta);
            if (delta > 0.02f) glowingPixels++;
        }
        float peak = ReadMax(Draw(true, Hdr, 0)).Rgb;
        float tightPeak = ReadMax(Draw(true, TightHeadroom, 0)).Rgb;
        float noHeadroomPeak = ReadMax(Draw(true, PaperWhite, 0)).Rgb;

        var linesField = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var activeLine = ((List<RenderLyricsLine>)linesField.GetValue(_lyrics)!)[0];
        foreach (var character in activeLine.PrimaryRenderChars)
            character.GlowTransition.JumpTo(0.4);
        float fadingGlowPeak = ReadMax(Draw(true, Hdr, 0)).Rgb;
        foreach (var character in activeLine.PrimaryRenderChars)
            character.GlowTransition.JumpTo(0);
        float settledGlowPeak = ReadMax(Draw(true, Hdr, 0)).Rgb;
        var glowSettings = MeasureGlowSettings();

        SetSettings(0, Colors.Black);
        Step(768);
        byte[] plainBlack = Draw(true, Hdr, Hdr.White);
        byte[] blackMask = Draw(false);
        StartGlow(Colors.Black);
        float blackGlowPeak = ReadMax(Draw(true, Hdr, 0)).Rgb;
        byte[] litBlack = Draw(true, Hdr, Hdr.White);
        float darkHaloLoss = 0;
        int darkHaloPixels = 0;
        for (int i = 0; i < litBlack.Length; i += 8)
        {
            if (ReadHalf(blackMask, i + 6) > 0.001f) continue;
            float amount = Luminance(plainBlack, i) - Luminance(litBlack, i);
            darkHaloLoss = Math.Max(darkHaloLoss, amount);
            if (amount > 0.02f) darkHaloPixels++;
        }
        var custom = Windows.UI.Color.FromArgb(255, 236, 145, 85);
        StartGlow(custom);
        var customGlow = ReadOpaqueInk(Draw(true, Hdr, 0));
        float customGlowHueDrift = Vector3.Distance(Vector3.Normalize(customGlow), Vector3.Normalize(Hdr.GetRgb(custom)));
        if (!float.IsFinite(customGlowHueDrift))
            throw new InvalidOperationException("Custom ink was invisible during HDR glow.");

        return new(played, unplayed, before, after, secondWord, maxCoreLoss, maxBackgroundLoss, maxGlow,
            (float)glowingPixels / (width * height), peak, tightPeak, noHeadroomPeak,
            raisedWhiteInk, blackGlowPeak, darkHaloLoss, (float)darkHaloPixels / (width * height),
            customGlowHueDrift, fadingGlowPeak, settledGlowPeak, glowSettings);

        static void SetSettings(double glow, Windows.UI.Color? color = null)
            => LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, color != Colors.Black,
                1, 0, glow, 0, 0, 100, false, false, 0.5, 0.7, 0,
                EasingType.Sine, EaseMode.Out, 0.35, 60, color.HasValue && color != Colors.Black, color ?? Colors.White));

        void StartGlow(Windows.UI.Color? color = null, double amount = 7)
        {
            SetSettings(amount, color);
            Step(1200);
            for (long time = 0; time <= 768; time += 4)
            {
                position = time;
                _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(4));
            }
        }

        LyricGlowComparison MeasureGlowSettings()
        {
            StartGlow(amount: 5);
            Step(2020);
            var characters = activeLine.PrimaryRenderChars;
            foreach (var character in characters) character.ProgressPlayed = 1;

            // Match the SDR surface format and render the same exact playback clock and character geometry.
            using var sdrTarget = new CanvasRenderTarget(device, width, height, 96,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);
            foreach (var character in characters) character.GlowTransition.JumpTo(0);
            byte[] mask = Draw(false);
            byte[] sdrMask = DrawSdr();
            foreach (var character in characters) character.GlowTransition.JumpTo(5);
            byte[] sdrGlow = DrawSdr();
            byte[] floatSdrGlow = Draw(false, default(PlaybackHdrColor));

            // Publish a settings snapshot without advancing the clock. The geometry and blur radius stay
            // frozen, so a reference-white cap can isolate RGB gain with the same active blur mask.
            SetSettings(0);
            byte[] disabled = Draw(false);
            SetSettings(double.NaN);
            if (!Draw(false).AsSpan().SequenceEqual(disabled))
                throw new InvalidOperationException("A non-finite lyric glow setting enabled glow.");
            SetSettings(0.5);
            foreach (var character in characters) character.GlowTransition.JumpTo(0.5);
            byte[] amount05 = Draw(false);
            foreach (var character in characters) character.GlowTransition.JumpTo(5);
            SetSettings(5);
            byte[] noGain = Draw(false, PaperWhite);
            byte[] amount5 = Draw(false);
            byte[] tight5 = Draw(false, TightHeadroom);
            SetSettings(10);
            byte[] amount10 = Draw(false);
            byte[] tight10 = Draw(false, TightHeadroom);
            byte[] brightNoGain = Draw(true, PaperWhite, 5.5f);
            byte[] brightWithGain = Draw(true, Hdr, 5.5f);
            SetSettings(11);
            if (!Draw(false).AsSpan().SequenceEqual(amount10))
                throw new InvalidOperationException("The lyric glow setting exceeded its 10-point range.");
            float hdrAlphaShift = 0, sdrAlphaDelta = 0, floatSdrAlphaDelta = 0, brightLoss = 0, outsideGlow = 0;
            for (int i = 6; i < mask.Length; i += 8)
            {
                float alpha = ReadHalf(noGain, i);
                hdrAlphaShift = Math.Max(hdrAlphaShift, Math.Abs(ReadHalf(amount5, i) - alpha));
                hdrAlphaShift = Math.Max(hdrAlphaShift, Math.Abs(ReadHalf(amount10, i) - alpha));
                hdrAlphaShift = Math.Max(hdrAlphaShift, Math.Abs(ReadHalf(tight5, i) - alpha));
                hdrAlphaShift = Math.Max(hdrAlphaShift, Math.Abs(ReadHalf(tight10, i) - alpha));
                if (ReadHalf(mask, i) <= .001f && sdrMask[i / 2] == 0)
                {
                    sdrAlphaDelta = Math.Max(sdrAlphaDelta, Math.Abs(sdrGlow[i / 2] / 255f - alpha));
                    floatSdrAlphaDelta = Math.Max(floatSdrAlphaDelta, Math.Abs(ReadHalf(floatSdrGlow, i) - alpha));
                }
                float brightRise = Luminance(brightWithGain, i - 6) - Luminance(brightNoGain, i - 6);
                brightLoss = Math.Max(brightLoss, -brightRise);
                if (alpha == 0) outsideGlow = Math.Max(outsideGlow, Math.Abs(brightRise));
            }

            var result = new LyricGlowComparison(Measure(sdrGlow, true), Measure(disabled), Measure(noGain),
                Measure(amount05), Measure(amount5), Measure(amount10), Measure(tight5), Measure(tight10),
                hdrAlphaShift, sdrAlphaDelta, floatSdrAlphaDelta, brightLoss, outsideGlow);
            if (Math.Abs(result.Disabled.Core - 1) > .006f || result.Disabled.HaloPixels != 0
                || Math.Abs(result.NoGain.Core - 1) > .006f
                || Math.Abs(result.Amount05.Core - 1.1f) > .006f
                || Math.Abs(result.Amount5.Core - 2f) > .006f
                || Math.Abs(result.Amount10.Core - 3f) > .006f
                || Math.Abs(result.Tight5.Core - 1.015f) > .006f
                || Math.Abs(result.Tight10.Core - 1.03f) > .006f
                || result.Amount5.CoreLoss > .015f || result.Tight10.CoreLoss > .015f
                || result.Amount5.HaloEnergy <= result.NoGain.HaloEnergy
                || result.Amount5.HaloEnergy <= result.Sdr.HaloEnergy
                || result.Amount10.HaloEnergy <= result.Amount5.HaloEnergy
                || result.MaximumHdrAlphaShift > .006f || result.MaximumSdrAlphaDelta > .060f
                || result.SamePrecisionSdrAlphaDelta > .012f
                || result.BrightBackgroundLoss > .01f || result.BrightBackgroundDriftOutsideHalo > .001f)
                throw new InvalidOperationException($"Lyric glow changed alpha coverage or missed the brightness mapping: {result}.");
            return result;

            byte[] DrawSdr()
            {
                using (var drawing = sdrTarget.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Transparent);
                    using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                    _lyrics.OnDraw(sdrTarget, scope.Context, default);
                }
                return sdrTarget.GetPixelBytes();
            }

            GlowSpread Measure(byte[] pixels, bool sdr = false)
            {
                const float threshold = .01f; // Measure only at least 1% of relative paper white.
                float core = 0, loss = 0, maxHalo = 0, energy = 0;
                int area = 0;
                for (int i = 0; i < mask.Length; i += 8)
                {
                    float alpha = ReadHalf(mask, i + 6);
                    float baseline = sdr ? DecodeSrgb(sdrMask[i / 2 + 2] / 255f) : Luminance(mask, i) / Hdr.White;
                    float sample = sdr ? DecodeSrgb(pixels[i / 2 + 2] / 255f) : Luminance(pixels, i) / Hdr.White;
                    if (alpha >= .99f)
                    {
                        core = Math.Max(core, sample);
                        loss = Math.Max(loss, baseline - sample);
                    }
                    if (alpha > .001f || sdrMask[i / 2 + 3] != 0) continue;
                    float added = sample - baseline;
                    maxHalo = Math.Max(maxHalo, added);
                    energy += Math.Max(0, added);
                    if (added > threshold) area++;
                }
                return new(core, loss, maxHalo, energy, area);
            }

            static float DecodeSrgb(float value) => value <= .04045f ? value / 12.92f :
                MathF.Pow((value + .055f) / 1.055f, 2.4f);
        }

        void Step(long time)
        {
            position = time;
            for (int i = 0; i < 45; i++) _lyrics.OnUpdate(target, Delta);
        }

        byte[] Draw(bool drawBackdrop, PlaybackHdrColor? hdr = null, float brightness = 5.5f)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(drawBackdrop ? Colors.Black : Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                if (drawBackdrop && brightness > 0)
                {
                    backdrop.ColorHdr = new Vector4(brightness, brightness, brightness, 1f);
                    drawing.FillRectangle(0, 0, width, height, backdrop);
                }
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                _lyrics.OnDraw(target, scope.Context, hdr ?? Hdr);
            }
            return target.GetPixelBytes();
        }

        static float MaxInk(byte[] pixels, int xStart, int xEnd, float minimumAlpha, bool normalizeCoverage = true)
        {
            float result = 0;
            for (int y = 0; y < height; y++)
            for (int x = xStart; x < xEnd; x++)
            {
                int offset = (y * width + x) * 8;
                float alpha = ReadHalf(pixels, offset + 6);
                if (alpha >= minimumAlpha)
                    result = Math.Max(result, Luminance(pixels, offset) / (normalizeCoverage ? alpha : 1));
            }
            return result;
        }
    }

    private object MeasureLyricHoverContrast(CanvasDevice device)
    {
        using var target = CreateFloatTarget(device, 1100, 700);
        using var surface = new CanvasSolidColorBrush(device, Colors.Black);
        var hoverIndex = typeof(LyricsRenderCoordinator).GetField("_hoveredLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;
        SetUpLyrics();

        var results = new List<object>();
        foreach (PlaybackHdrColor hdr in new[] { Hdr, default })
        {
            foreach (var (isDark, backdropFraction) in new[] { (true, 0f), (false, 0.75f), (false, 1f) })
            {
                LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, isDark,
                    1, 0, 0, 0, 0, 100, false, false, 0.5, 0.7, 0, EasingType.Sine, EaseMode.Out,
                    0.35, 60, false, Colors.White));
                for (int frame = 0; frame < 40; frame++)
                    _lyrics.OnUpdate(target, Delta);
                float paperWhite = hdr.IsEnabled ? hdr.White : 1f;
                float background = paperWhite * backdropFraction;
                byte[] mask = Draw(false, false, hdr, background);
                byte[] clear = Draw(true, false, hdr, background);
                byte[] hover = Draw(true, true, hdr, background);
                float positive = 0, negative = 0, glyphShift = 0;
                int opaqueInk = 0;
                for (int i = 0; i < mask.Length; i += 8)
                {
                    float alpha = ReadHalf(mask, i + 6);
                    float delta = Luminance(hover, i) - Luminance(clear, i);
                    if (alpha <= 0.001f)
                    {
                        positive = Math.Max(positive, delta);
                        negative = Math.Max(negative, -delta);
                    }
                    if (alpha >= 0.999f)
                    {
                        opaqueInk++;
                        glyphShift = Math.Max(glyphShift, Math.Abs(delta));
                    }
                }
                var result = new { IsHdr = hdr.IsEnabled, IsDark = isDark, Background = background, PositiveBackdrop = positive,
                    NegativeBackdrop = negative, GlyphShift = glyphShift, OpaqueInk = opaqueInk };
                if (opaqueInk == 0 || glyphShift > 0.01f
                    || isDark && (positive < paperWhite * 0.10f || negative > 0.01f)
                    || !isDark && (negative < paperWhite * 0.045f
                        || (backdropFraction < 1f ? positive < paperWhite * 0.02f : positive > 0.01f)))
                    throw new InvalidOperationException($"Incorrect lyric hover color or z-order: {result}.");
                results.Add(result);
            }
        }

        hoverIndex.SetValue(_lyrics, -1);
        return results;

        void SetUpLyrics()
        {
            TimeProgressBus.SetClock(static () => 850);
            IsPlayingBus.Publish(true);
            LyricsFontSizeBus.Publish(52);
            UILyricsBus.Publish([new LyricLine
            {
                EndMs = 1000,
                HighlightEndMs = 1000,
                Words = [new() { Word = "MMMM", StartMs = 0, DurationMs = 1000 }]
            }]);
        }

        byte[] Draw(bool paintBackground, bool isHovered, PlaybackHdrColor hdr, float background)
        {
            hoverIndex.SetValue(_lyrics, isHovered ? 0 : -1);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(paintBackground ? Colors.Black : Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                if (paintBackground && background > 0)
                {
                    surface.ColorHdr = new Vector4(background, background, background, 1f);
                    drawing.FillRectangle(0, 0, 1100, 700, surface);
                }
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                _lyrics.OnDraw(target, scope.Context, hdr);
            }
            return target.GetPixelBytes();
        }
    }

    private static float Luminance(byte[] pixels, int offset)
        => ReadHalf(pixels, offset) * 0.2126f + ReadHalf(pixels, offset + 2) * 0.7152f
           + ReadHalf(pixels, offset + 4) * 0.0722f;
}
