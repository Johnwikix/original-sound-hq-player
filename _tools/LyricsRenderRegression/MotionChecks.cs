using System.Reflection;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Messages;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;

/// <summary>Exercises shared motion/effect timing on the real Win2D game-loop thread.</summary>
internal static class MotionChecks
{
    private static readonly MethodInfo Update = typeof(LyricsRenderCoordinator).GetMethod("UpdateCore", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Lines = typeof(LyricsRenderCoordinator).GetField("_renderLines", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly FieldInfo Timing = typeof(LyricsRenderCoordinator).GetField("_lineScrollTiming", BindingFlags.Instance | BindingFlags.NonPublic)!;

    internal static void Run(CanvasAnimatedControl canvas)
    {
        Check(canvas.HasGameLoopThreadAccess, "Motion integration must run on the real game-loop thread.");
        long position = 0;
        TimeProgressBus.SetClock(() => position);
        var coordinator = new LyricsRenderCoordinator { Canvas = canvas, LyricsRegion = new Rect(0, 0, 600, 400) };
        try
        {
            coordinator.Attach();
            coordinator.OnCreateResources();
            long[] starts = [0, 1500, 3000, 3300, 3500, 5000, 6500, 8000];
            UILyricsBus.Publish(starts.Select((start, index) => new LyricLine
            {
                StartMs = start,
                EndMs = index + 1 < starts.Length ? starts[index + 1] : 10000,
                Words = [new LyricWord { Word = "Line " + index, StartMs = start, DurationMs = 10000 }],
                TransLateText = "Translation " + index
            }).ToList());
            IsPlayingBus.Publish(true);
            using var target = new CanvasRenderTarget(canvas.Device, 600, 400, 96);
            // OnDraw consumes the publication at the production frame boundary before manual stepping.
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            void Step(int frame)
            {
                position = (long)Math.Round(frame * 1000.0 / 60);
                Update.Invoke(coordinator, [canvas, TimeSpan.FromSeconds(1.0 / 60)]);
            }
            for (int frame = 0; frame < 90; frame++) Step(frame);
            var lines = (List<RenderLyricsLine>)Lines.GetValue(coordinator)!;
            var focus = lines[1];
            double offset = focus.ScrollMotion.Value;
            double scale = focus.ScaleTransition.Value;
            double blur = focus.BlurAmountTransition.Value;
            double played = focus.PlayedPrimaryOpacityTransition.Value;
            double unplayed = focus.UnplayedPrimaryOpacityTransition.Value;
            double secondary = focus.SecondaryOpacityTransition.Value;
            Step(90);
            var timing = (LyricScrollTiming)Timing.GetValue(coordinator)!;
            Check(timing.WaveOriginIndex == 0 && timing.GetLineTiming(1).Delay > 0,
                "The top visible row must lead; the playing row must wait for propagation.");
            Check(focus.ScrollMotion.Value == offset && lines[0].ScrollMotion.Value < offset,
                "Only the leading row should move during the focus startup delay.");
            Check(focus.ScaleTransition.Value == scale && focus.BlurAmountTransition.Value == blur
                && focus.PlayedPrimaryOpacityTransition.Value == played
                && focus.UnplayedPrimaryOpacityTransition.Value == unplayed
                && focus.SecondaryOpacityTransition.Value == secondary,
                "Scale, blur and all opacity channels must hold alongside displacement.");
            for (int frame = 91; frame <= 97; frame++) Step(frame);
            Check(focus.ScrollMotion.Value < offset && focus.ScaleTransition.Value > scale
                && focus.BlurAmountTransition.Value < blur && focus.PlayedPrimaryOpacityTransition.Value > played,
                "Motion and line effects must resume after the common delay.");
            Check(lines[2].UnplayedPrimaryOpacityTransition.Value > 0,
                "Initially transparent rows must progress instead of restarting their delay every frame.");

            for (int frame = 98; frame <= 180; frame++) Step(frame);
            timing = (LyricScrollTiming)Timing.GetValue(coordinator)!;
            Check(timing.IntervalSeconds == 0.3 && timing.GetLineTiming(2).Delay < 0.02
                && timing.GetLineTiming(2).Duration == 0.5,
                "300 ms lyrics must retain reduced staggering and stable spring response.");
            for (int frame = 181; frame <= 198; frame++) Step(frame);
            timing = (LyricScrollTiming)Timing.GetValue(coordinator)!;
            Check(timing.IntervalSeconds == 0.2 && timing.GetLineTiming(7).Delay == 0,
                "200 ms lyrics must disable staggering for the whole visible wave.");

            position = 6500;
            Update.Invoke(coordinator, [canvas, TimeSpan.FromSeconds(1.0 / 60)]);
            timing = (LyricScrollTiming)Timing.GetValue(coordinator)!;
            Check(timing.WaveOriginIndex == -1 && timing.GetLineTiming(6).Delay == 0,
                "Seeking must clear the previous entrance's delayed plan.");
            coordinator.InvalidateStyle();
            Update.Invoke(coordinator, [canvas, TimeSpan.Zero]);
            Check(lines.All(line => !line.ScrollMotion.IsMoving), "Relayout must discard every pending destination.");
            using (var drawing = target.CreateDrawingSession()) coordinator.OnDraw(canvas, drawing);
            Check(lines.Any(line => line.CachedFill is not null), "The modified effects must render using real Win2D resources.");
        }
        finally
        {
            coordinator.PrepareForShutdown();
            TimeProgressBus.SetClock(null);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
