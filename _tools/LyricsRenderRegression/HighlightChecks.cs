using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Checks highlight retention and selection on the real Win2D game-loop thread.</summary>
internal static class HighlightChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Focus = typeof(LyricsRenderCoordinator).GetField("_currentLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        Check(canvas.HasGameLoopThreadAccess, "Highlight integration must run on the real game-loop thread.");
        long position = 83000;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            UILyricsBus.Publish([
                Line(0, 1000, 77396, "Earlier"),
                Line(77396, 83675, 96652, "It carries on"),
                Line(96652, 98000, 100000, "Next")
            ]);
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            void Step(long time)
            {
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromSeconds(1.0 / 60)]);
                using var drawing = target.CreateDrawingSession();
                coordinator.OnDraw(canvas, drawing);
            }

            foreach (long time in new long[] { 83000, 83675, 85000, 96651 })
            {
                Step(time);
                Check((int)Focus.GetValue(coordinator)! == 1 && lines[1].IsPlayingLastFrame,
                    $"The current row must retain its highlight at {time} ms before the next entrance.");
            }
            Check(lines[1].EndMs == 83675 && lines[1].PrimaryRenderSyllables[^1].EndMs == 83675
                && lines[1].GetPlayProgress(85000) == 1,
                "Highlight retention must not stretch source or word timing.");
            Check(lines[1].PrimaryRenderChars.All(character => character.GetPlayProgress(85000) == 1 && !character.GetIsPlaying(85000)),
                "Words must finish instead of restarting their effects during the display hold.");
            CheckCompletedFill(canvas, lines[1]);
            Step(96652);
            Check((int)Focus.GetValue(coordinator)! == 2 && !lines[1].IsPlayingLastFrame && lines[2].IsPlayingLastFrame,
                "Highlight and focus must switch together at the next entrance.");
            Step(85000);
            Check((int)Focus.GetValue(coordinator)! == 1 && lines[1].IsPlayingLastFrame,
                "Seeking backward into a gap must recover that row, independently of selection history.");
            IsPlayingBus.Publish(false);
            Step(90000);
            Check(lines[1].IsPlayingLastFrame, "Pausing in a gap must retain the same highlighted row.");
            Step(50000);
            Check((int)Focus.GetValue(coordinator)! == 0 && lines[0].IsPlayingLastFrame && !lines[1].IsPlayingLastFrame,
                "Seeking to an earlier gap must select the row belonging to that display interval.");
            Step(99999);
            Check(lines[2].IsPlayingLastFrame, "The last row must retain the supplied song-end display interval.");
            Step(100000);
            Check(!lines[2].IsPlayingLastFrame, "The final display boundary must remain exclusive.");

            var overlap = new RenderLyricsLine();
            overlap.LoadFromLyricLine(Line(1000, 4000, 2500, "Overlap"), 2500);
            Check(overlap.GetIsHighlighted(3500) && overlap.EndMs == 4000,
                "A later entrance must not truncate a genuinely overlapping lyric.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static void CheckCompletedFill(CanvasAnimatedControl canvas, RenderLyricsLine line)
    {
        line.PlayedPrimaryOpacityTransition.JumpTo(1);
        line.UnplayedPrimaryOpacityTransition.JumpTo(1);
        line.BlurAmountTransition.JumpTo(0);
        line.UnplayedFillTint!.Color = Microsoft.UI.Colors.Black;
        var renderer = new LyricsLineRenderer
        {
            Line = line,
            IsPlaying = line.GetIsHighlighted(85000),
            CurrentProgressMs = 85000,
            PlayedFillColor = Microsoft.UI.Colors.White,
            UnplayedFillColor = Microsoft.UI.Colors.Black
        };
        using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
        using (var drawing = target.CreateDrawingSession())
        {
            drawing.Clear(Microsoft.UI.Colors.Transparent);
            renderer.Draw(canvas, drawing);
        }
        Check(target.GetPixelColors().Any(pixel => pixel.R > 128 && pixel.A > 128),
            "Real Win2D pixels must retain the completed white fill during the gap.");
    }

    private static LyricLine Line(double start, double end, double highlightEnd, string text) => new()
    {
        StartMs = start,
        EndMs = end,
        HighlightEndMs = highlightEnd,
        Words = [new LyricWord { Word = text, StartMs = start, DurationMs = end - start }]
    };

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
