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

internal sealed partial class ProbeApplication
{
    private static readonly PlaybackHdrColor TightHeadroom = PlaybackHdrColor.FromNits(200, 206);

    private sealed record BackgroundGradient(float Peak, float GainP95, float NeighborStepP99, float Above105PercentWhite);
    private sealed record LyricSweep(float PlayedCore, float UnplayedInk, float BeforeWordEnd, float AfterWordEnd,
        float NextWordCore, float MaximumCoreLossWithGlow, float MaximumBackgroundLossWithGlow,
        float MaximumGlowOutsideInk, float GlowArea, float GlowPeak, float TightGlowPeak, float NoHeadroomPeak,
        float RaisedWhiteInk, float BlackGlowPeak, float DarkHaloLoss, float DarkHaloArea,
        float CustomGlowHueDrift, float FadingGlowPeak, float SettledGlowPeak);

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
            EndMs = 2000,
            HighlightEndMs = 2000,
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
        byte[] plainOverBackground = Draw(true);
        var raisedWhite = PlaybackHdrColor.FromNits(320, 600);
        float raisedWhiteInk = ReadMax(Draw(true, raisedWhite, 0)).Rgb;
        StartGlow();
        byte[] glowing = Draw(true);
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
            customGlowHueDrift, fadingGlowPeak, settledGlowPeak);

        static void SetSettings(double glow, Windows.UI.Color? color = null)
            => LyricsSettingsBus.Publish(new("Consolas", CanvasHorizontalAlignment.Left, color != Colors.Black,
                1, 0, glow, 0, 0, 100, false, false, 0.5, 0.7, 0,
                EasingType.Sine, EaseMode.Out, 0.35, 60, color.HasValue && color != Colors.Black, color ?? Colors.White));

        void StartGlow(Windows.UI.Color? color = null)
        {
            SetSettings(7.0, color);
            Step(1200);
            for (long time = 0; time <= 768; time += 4)
            {
                position = time;
                _lyrics.OnUpdate(target, TimeSpan.FromMilliseconds(4));
            }
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

    private static float Luminance(byte[] pixels, int offset)
        => ReadHalf(pixels, offset) * 0.2126f + ReadHalf(pixels, offset + 2) * 0.7152f
           + ReadHalf(pixels, offset + 4) * 0.0722f;
}
