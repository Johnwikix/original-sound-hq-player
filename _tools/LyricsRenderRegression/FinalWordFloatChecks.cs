using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Checks the reported LRC words through production parsing, projection and native drawing.</summary>
internal static class FinalWordFloatChecks
{
    // The last timestamp only closes the supplied excerpt so "mess" has a next-row boundary.
    internal const string Sample = """
        [00:00.000] 作词 : Life Awaits
        [00:00.058] 作曲 : Life Awaits
        [00:00.117]Captivated in repetition
        [00:04.297]Isolated, but I don't want to be saved
        [00:08.999]Take it all away, take it all away
        [00:11.507]It's hard to wrap my head
        [00:13.701]Take it all away, take it all away
        [00:15.686]Around the tangled mess
        [00:20.000]End of test excerpt
        """;

    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        var failures = new List<string>();
        foreach (var mode in new[] { EaseMode.FlowWave, EaseMode.Out })
        foreach (string text in new[] { "repetition", "mess" })
        {
            long position = 0;
            TimeProgressBus.SetClock(() => position);
            var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
            try
            {
                coordinator.Attach();
                coordinator.OnCreateResources();
                LyricsSettingsBus.Publish(new("Segoe UI", CanvasHorizontalAlignment.Left, false, 1, 0,
                    5, 5, 110, 500, false, false, 0.3, 0.5, 0, EasingType.Sine, mode,
                    0.33, 120, false, Microsoft.UI.Colors.White));
                UILyricsBus.Publish(lyrics);
                IsPlayingBus.Publish(true);
                using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
                using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
                var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
                var line = lines.Single(row => row.PrimaryText.EndsWith(text, StringComparison.Ordinal));
                var word = line.PrimaryRenderSyllables.Last();
                if (word.Text != text || word.DurationMs < 500)
                    throw new InvalidOperationException($"The reported final word must reproduce a long syllable: {text}.");
                var firstCharacter = word.ChildrenRenderLyricsChars.First();
                var lastCharacter = word.ChildrenRenderLyricsChars.Last();
                var update = Update.CreateDelegate<Action<ICanvasResourceCreator, TimeSpan>>(coordinator);
                double before = double.NaN, after = double.NaN, lastAfter = double.NaN;
                double glowAtEnd = 0, scaleAtEnd = 1;
                for (position = 8; position < line.HighlightEndMs; position += 8)
                {
                    update(canvas, TimeSpan.FromMilliseconds(8));
                    using var drawing = target.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    coordinator.OnDraw(canvas, drawing);
                    if (position < word.StartMs) before = firstCharacter.FloatTransition.Value;
                    if (double.IsNaN(after) && position >= word.StartMs + 460)
                        after = firstCharacter.FloatTransition.Value;
                    if (position >= lastCharacter.StartMs + 100 && double.IsNaN(lastAfter))
                        lastAfter = lastCharacter.FloatTransition.Value;
                    glowAtEnd = firstCharacter.GlowTransition.Value;
                    scaleAtEnd = firstCharacter.ScaleTransition.Value;
                }
                if (before < 4.9 || double.IsNaN(after) || after >= 2
                    || double.IsNaN(lastAfter) || lastAfter >= 4.9)
                    failures.Add($"Final word must rise as it plays, including its last character: mode={mode}, word={text}, before={before:F3}, after={after:F3}, lastAfter={lastAfter:F3}.");
                if (mode == EaseMode.FlowWave && (glowAtEnd < 4.9 || scaleAtEnd < 1.09))
                    failures.Add($"Final-word glow/scale hold must survive independently of float: word={text}, glow={glowAtEnd:F3}, scale={scaleAtEnd:F3}.");
            }
            finally
            {
                coordinator.PrepareForShutdown();
                TimeProgressBus.SetClock(null);
            }
        }
        if (failures.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
    }
}
