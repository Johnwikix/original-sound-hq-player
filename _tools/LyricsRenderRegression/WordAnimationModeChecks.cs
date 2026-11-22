using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Enums;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;

internal static class WordAnimationModeChecks
{
    internal const string TtmlSample = """
        <tt xmlns="http://www.w3.org/ns/ttml"><body><div>
        <p begin="0s" end="4s"><span>Hello</span><span> styled verse</span></p>
        <p begin="4s" end="8s"><span begin="4s" end="6s">Fi<span>rst</span> </span><span begin="6.5s" end="8s"><span>Second verse</span></span></p>
        </div></body></tt>
        """;

    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> plainLyrics, List<LyricLine> ttmlLyrics)
    {
        var plain = plainLyrics.Single(line => line.Words.Any(word => word.Word == "repetition"));
        Check(plain.HasRealWordTiming == false && plain.Words.Any(word => word.DurationMs > 0),
            "Plain LRC must distinguish source timing from estimated word durations.");
        VerifyLine(canvas, plain, expectedAutoSweep: false);

        Check(ttmlLyrics.Count == 2 && ttmlLyrics[0].HasRealWordTiming == false
                && string.Concat(ttmlLyrics[0].Words.Select(word => word.Word)) == "Hello styled verse",
            "Untimed formatting spans must use estimated line timing without dropping text.");
        var timed = ttmlLyrics[1];
        Check(timed.HasRealWordTiming == true && timed.Words.Count == 2
                && timed.Words[0].Word == "First " && timed.Words[0].StartMs == 4000 && timed.Words[0].DurationMs == 2000
                && timed.Words[1].Word == "Second verse" && timed.Words[1].StartMs == 6500,
            "A timed parent span must keep its nested text and original syllable boundaries.");
        VerifyLine(canvas, ttmlLyrics[0], expectedAutoSweep: false);
        VerifyLine(canvas, timed, expectedAutoSweep: true);
        VerifyLine(canvas, new LyricLine
        {
            StartMs = 1000,
            EndMs = 5000,
            HasRealWordTiming = false,
            Words = [new LyricWord { Word = "Untimed verse", StartMs = 1000 }]
        }, expectedAutoSweep: false);
    }

    private static void VerifyLine(CanvasAnimatedControl canvas, LyricLine source, bool expectedAutoSweep)
    {
        var line = new RenderLyricsLine();
        var renderer = new LyricsLineRenderer
        {
            Line = line,
            IsPlaying = true,
            PlayedFillColor = Colors.White,
            UnplayedFillColor = Colors.Black
        };
        try
        {
            line.LoadFromLyricLine(source, source.EndMs);
            line.RecreateTextLayout(canvas, 28, 12, "Segoe UI", 180, 250, CanvasHorizontalAlignment.Left);
            line.RecreateRenderChars(0);
            line.EnsureCaches(canvas, 0);

            using var target = new CanvasRenderTarget(canvas.Device, 230, 250, 96);
            var animator = new LyricsAnimator();
            var scroll = new ValueTransition<double>(0, (from, to, progress) => from + (to - from) * progress, 0.2);
            var lines = new[] { line };
            int animationVersion = 0;
            double now = source.StartMs + (source.EndMs - source.StartMs) * 0.4;
            void Update(WordByWordEffectMode mode)
            {
                animator.UpdateLines(lines, 0, 0, 0, 230, 250, 0, 0.5,
                    false, false, false, 1, 1, 1, true, 6, 0,
                    true, 5, 120, true, 1.12, 0, 0, scroll,
                    TimeSpan.FromMilliseconds(120), false, false, false, false, now, ++animationVersion,
                    wordByWordEffectMode: mode);
                line.PlayedPrimaryOpacityTransition.JumpTo(1);
                line.UnplayedPrimaryOpacityTransition.JumpTo(1);
            }

            int LitPixels(WordByWordEffectMode mode)
            {
                renderer.WordByWordEffectMode = mode;
                renderer.CurrentProgressMs = now;
                using (var drawing = target.CreateDrawingSession())
                {
                    drawing.Clear(Colors.Transparent);
                    renderer.Draw(canvas, drawing);
                }
                return target.GetPixelColors().Count(pixel => pixel.R > 128 && pixel.A > 128);
            }

            Update(WordByWordEffectMode.Auto);
            int auto = LitPixels(WordByWordEffectMode.Auto);
            Update(WordByWordEffectMode.Always);
            int always = LitPixels(WordByWordEffectMode.Always);
            Check(line.PrimaryRenderChars.Any(character => character.GlowTransition.Value > 0
                    && character.ScaleTransition.Value > 1),
                "Always must drive authored and estimated timed emphasis.");
            Check(always > 0, "Always must light part of the line during playback.");

            Update(WordByWordEffectMode.Never);
            int never = LitPixels(WordByWordEffectMode.Never);
            Check(never > always && auto == (expectedAutoSweep ? always : never),
                $"Expected partial and whole-line fills. Auto={auto}, Always={always}, Never={never}.");
            Check(!line.IsWordEffectsRetiring && line.PrimaryRenderChars.All(character =>
                character.GlowTransition.Value == 0 && !character.GlowTransition.IsTransitioning
                && character.ScaleTransition.Value == 1 && !character.ScaleTransition.IsTransitioning
                && character.FloatTransition.Value == 0 && !character.FloatTransition.IsTransitioning),
                "Switching from Always to Never must clear active word effects.");

            Update(WordByWordEffectMode.Always);
            Check(LitPixels(WordByWordEffectMode.Always) == always
                  && line.PrimaryRenderChars.Any(character => character.GlowTransition.Value > 0),
                "Switching back to Always on the same line must restore the sweep and active emphasis.");
        }
        finally
        {
            renderer.ClearSharedResources();
            line.DisposeCaches();
            line.DisposeTextGeometry();
            line.DisposeTextLayout();
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
