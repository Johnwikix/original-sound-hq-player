using System.Numerics;
using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Interop;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Brushes;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;

internal sealed partial class ProbeApplication
{
    private sealed record LyricHaloFrame(float Peak, float AlphaPeak, float NeighborStep, int NearPaperWhitePixels,
        int HaloPixels, float HaloEnergy, float PrimaryCore);
    private sealed record BlurTailCounts(int PrimaryOutsideOldBounds, int AuxiliaryOutsideOldBounds);

    private object MeasureLyricHaloClipping(CanvasDevice device)
    {
        const int width = 1100, height = 700;
        using var target = CreateFloatTarget(device, width, height);
        var backdrop = new Vector4(.16f * Hdr.White, .24f * Hdr.White, .52f * Hdr.White, 1);
        using var brush = CanvasSolidColorBrush.CreateHdr(device, backdrop);
        LyricsFontSizeBus.Publish(30);
        var lyrics = new LyricLine
        {
            EndMs = 1800,
            HighlightEndMs = 1800,
            PronunciationText = "なにがわるい",
            TransLateText = "又有什么不好的",
            Words = [new() { Word = "なにが悪い", StartMs = 0, DurationMs = 600 }]
        };
        UILyricsBus.Publish([lyrics]);
        PublishSettings(10);
        IsPlayingBus.Publish(true);
        TimeProgressBus.SetClock(static () => 800);
        for (int i = 0; i < 45; i++) _lyrics.OnUpdate(target, Delta);

        var lines = (List<RenderLyricsLine>)typeof(LyricsRenderCoordinator)
            .GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_lyrics)!;
        var line = lines[0];
        if (line.PrimaryRenderChars.Count < 5 || line.PronunciationLayer.Layout is null || line.TranslationLayer.Layout is null)
            throw new InvalidOperationException("The timed Japanese fixture must have individual glyphs and both auxiliary tracks.");
        PublishSettings(0);
        SetGlow(0);
        var mask = Draw(Hdr, false);
        var basePurple = Draw(Hdr, true);
        PublishSettings(5);
        SetGlow(5);
        var standard = Measure(Draw(Hdr, true), basePurple, mask);
        PublishSettings(10);
        SetGlow(10);
        var active = Draw(Hdr, false);
        var purple = Draw(Hdr, true);
        var maximum = Measure(active, mask, mask);
        var maximumPurple = Measure(purple, basePurple, mask);

        typeof(RenderLyricsLine).GetProperty(nameof(RenderLyricsLine.IsWordEffectsRetiring))!.SetValue(line, true);
        foreach (var character in line.PrimaryRenderChars)
            typeof(RenderLyricsChar).GetProperty(nameof(RenderLyricsChar.GlowExitStartAmount))!.SetValue(character, 10d);
        SetGlow(5);
        line.BlurAmountTransition.JumpTo(8);
        var retirementPixels = Draw(Hdr, false);
        var retirement = Measure(retirementPixels, mask, mask);
        var ordinaryRetirementPixels = Draw(PaperWhite, false);
        var retirementNoGain = Measure(ordinaryRetirementPixels, mask, mask);
        float retirementAlphaShift = 0;
        for (int offset = 6; offset < retirementPixels.Length; offset += 8)
            retirementAlphaShift = Math.Max(retirementAlphaShift,
                Math.Abs(ReadHalf(retirementPixels, offset) - ReadHalf(ordinaryRetirementPixels, offset)));
        var retirementPurple = Measure(Draw(Hdr, true), basePurple, mask);
        var bounds = MeasureBlurredTrackBounds();
        if (Math.Abs(standard.PrimaryCore - 1.5f) > .01f || Math.Abs(maximum.PrimaryCore - 2f) > .01f
            || maximum.Peak > 1.05f || maximumPurple.NearPaperWhitePixels > 180
            || maximumPurple.NeighborStep > .075f || retirement.NeighborStep > .075f
            || retirementAlphaShift > .003f || retirement.PrimaryCore - retirementNoGain.PrimaryCore > .16f
            || retirement.HaloEnergy <= retirementNoGain.HaloEnergy
            || bounds.PrimaryOutsideOldBounds < 80 || bounds.AuxiliaryOutsideOldBounds < 80)
            throw new InvalidOperationException($"Japanese lyric halo masked or flattened its blur. Standard {standard}; maximum {maximum}; purple {maximumPurple}; retirement {retirement}; ordinary retirement {retirementNoGain}; alpha shift {retirementAlphaShift}; blur tails {bounds}.");

        return new
        {
            Standard = standard, Maximum = maximum, MaximumPurple = maximumPurple, Retirement = retirement,
            RetirementPurple = retirementPurple, RetirementNoGain = retirementNoGain, RetirementAlphaShift = retirementAlphaShift,
            BlurredTrackBounds = bounds
        };

        BlurTailCounts MeasureBlurredTrackBounds()
        {
            const float x = 200, y = 280, blur = 10;
            var renderer = new LyricsLineRenderer
            {
                Line = line, IsPlaying = false, PlayedFillColor = Colors.White, UnplayedFillColor = Colors.White
            };
            var setPlaybackColor = typeof(LyricsLineRenderer).GetMethod("SetPlaybackColor", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var drawAll = Enum.Parse(setPlaybackColor.GetParameters()[2].ParameterType, "All");
            setPlaybackColor.Invoke(renderer, [Hdr, 0d, drawAll]);
            var local = line.PrimaryTextLayout!.LayoutBounds;
            var primaryBounds = new Rect(local.X + line.CachedPrimaryPosition.X + x - 10,
                local.Y + line.CachedPrimaryPosition.Y + y - 10, local.Width + 20, local.Height + 20);
            line.PronunciationLayer.Reveal.JumpTo(0);
            line.TranslationLayer.Reveal.JumpTo(0);
            line.BlurAmountTransition.JumpTo(blur);
            byte[] inactive;
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                drawing.Transform = Matrix3x2.CreateTranslation(x, y);
                renderer.Draw(target, drawing);
            }
            inactive = target.GetPixelBytes();
            renderer.ClearSharedResources();

            var layer = line.TranslationLayer;
            layer.Reveal.JumpTo(1);
            var text = layer.Layout!.LayoutBounds;
            var auxiliaryBounds = new Rect(text.X + x - 10, text.Y + y - 5, text.Width + 20, layer.Height + 10);
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                layer.Draw(target, scope.Context, new(x, y), 1, blur, 0, Colors.White, Hdr);
            }

            int primaryOutsideOldBounds = CountBlurTail(inactive, primaryBounds);
            int auxiliaryOutsideOldBounds = CountBlurTail(target.GetPixelBytes(), auxiliaryBounds);
            return new(primaryOutsideOldBounds, auxiliaryOutsideOldBounds);

            static int CountBlurTail(byte[] pixels, Rect previousClip)
            {
                int count = 0;
                for (int row = 0; row < height; row++)
                for (int column = 0; column < width; column++)
                {
                    // Skip one full pixel around the boundary to exclude fractional clip antialiasing.
                    if (column >= previousClip.Left - 1 && column <= previousClip.Right + 1
                        && row >= previousClip.Top - 1 && row <= previousClip.Bottom + 1) continue;
                    if (ReadHalf(pixels, (row * width + column) * 8 + 6) > .01f) count++;
                }
                return count;
            }
        }

        static void PublishSettings(double amount) => LyricsSettingsBus.Publish(new("Yu Gothic UI",
            CanvasHorizontalAlignment.Left, true, 1, 8, amount, 0, 0, 100, false, false, .5, .7,
            0, EasingType.Sine, EaseMode.Out, .35, 60, false, Colors.White));

        void SetGlow(double radius)
        {
            foreach (var character in line.PrimaryRenderChars)
            {
                character.ProgressPlayed = 1;
                character.GlowTransition.JumpTo(radius);
            }
        }

        byte[] Draw(PlaybackHdrColor hdr, bool purple)
        {
            using (var drawing = target.CreateDrawingSession())
            {
                drawing.Clear(Colors.Transparent);
                drawing.EffectBufferPrecision = CanvasBufferPrecision.Precision16Float;
                if (purple) drawing.FillRectangle(0, 0, width, height, brush);
                using var scope = CanvasDrawingSessionScope.Borrow(drawing);
                _lyrics.OnDraw(target, scope.Context, hdr);
            }
            return target.GetPixelBytes();
        }

        static LyricHaloFrame Measure(byte[] pixels, byte[] background, byte[] mask)
        {
            float peak = 0, alphaPeak = 0, step = 0, energy = 0, core = 0;
            int bright = 0, lit = 0;
            for (int y = 1; y < height; y++)
            for (int x = 1; x < width; x++)
            {
                int i = (y * width + x) * 8;
                if (ReadHalf(mask, i + 6) >= .999f)
                    core = Math.Max(core, Luminance(pixels, i) / Hdr.White);
                if (!OutsideInk(i)) continue;
                float change = Delta(i);
                peak = Math.Max(peak, change);
                alphaPeak = Math.Max(alphaPeak, ReadHalf(pixels, i + 6) - ReadHalf(background, i + 6));
                energy += Math.Max(0, change);
                if (change > .01f) lit++;
                if (change > .01f && Luminance(pixels, i) >= .80f * Hdr.White) bright++;
                int left = i - 8;
                int above = i - width * 8;
                if (OutsideInk(left)) step = Math.Max(step, Math.Abs(change - Delta(left)));
                if (OutsideInk(above)) step = Math.Max(step, Math.Abs(change - Delta(above)));
            }
            return new(peak, alphaPeak, step, bright, lit, energy, core);

            bool OutsideInk(int offset) => ReadHalf(mask, offset + 6) <= .001f;
            float Delta(int offset) => (Luminance(pixels, offset) - Luminance(background, offset)) / Hdr.White;
        }
    }
}
