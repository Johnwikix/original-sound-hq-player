using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

internal static class EmptyTimestampChecks
{
    internal const string Sample = "[1000,1000]A(1000,400)(1400,0)(1400,0)B(1400,600)\n" +
        "[2000,500](2000,0)(2000,0)\n[2000,500](2000,0)(2000,0)\n[3000,1000]C(3000,1000)";
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Focus = typeof(LyricsRenderCoordinator).GetField("_currentLineIndex", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas, List<LyricLine> lyrics)
    {
        long position = 1000;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            UILyricsBus.Publish(lyrics);
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            if (lines.Count != 2 || lines[0].PrimaryText != "AB" || lines[0].PrimaryRenderSyllables.Count != 2)
                throw new InvalidOperationException("Empty rows or word groups reached the native renderer.");
            void Step(long time)
            {
                position = time;
                Update.Invoke(coordinator, [canvas, TimeSpan.FromSeconds(1.0 / 60)]);
                using var drawing = target.CreateDrawingSession();
                coordinator.OnDraw(canvas, drawing);
            }
            foreach (long time in new long[] { 2000, 2499, 2999 })
            {
                Step(time);
                if ((int)Focus.GetValue(coordinator)! != 0 || !lines[0].IsPlayingLastFrame || lines[0].CachedFill is null)
                    throw new InvalidOperationException($"Empty word rows interrupted native highlight at {time}ms.");
            }
            Step(3000);
            if ((int)Focus.GetValue(coordinator)! != 1 || lines[0].IsPlayingLastFrame || !lines[1].IsPlayingLastFrame)
                throw new InvalidOperationException("The next real row must enter at 3000ms.");
            IsPlayingBus.Publish(false);
            Step(2500);
            if ((int)Focus.GetValue(coordinator)! != 0 || !lines[0].IsPlayingLastFrame)
                throw new InvalidOperationException("Paused backward seek must recover the valid row across empty groups.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }
}
