using System.Diagnostics;
using System.Reflection;
using AnimatedWin2dControls.Controls;

internal sealed partial class ProbeApplication
{
    private static object MeasurePresentationCadence()
    {
        // Synthetic composition callbacks isolate clock drift from the host monitor's actual refresh rate.
        Type clockType = typeof(NowPlayingCanvas).Assembly.GetType("AnimatedWin2dControls.Interop.PresentationFrameClock", true)!;
        var tryAdvance = clockType.GetMethod("TryAdvance", BindingFlags.Instance | BindingFlags.NonPublic)!;
        long start = Stopwatch.Frequency;
        var results = new List<object>();
        foreach (var (display, target) in new[] { (60, 60), (90, 60), (120, 60), (144, 60), (144, 120), (165, 120), (60, 120), (480, 360) })
        {
            object clock = Activator.CreateInstance(clockType)!;
            int submitted = 0, previousSubmitted = 0;
            long previousTick = 0;
            const int seconds = 10;
            for (int callback = 0; callback < display * seconds; callback++)
            {
                long now = start + callback * Stopwatch.Frequency / display;
                if (previousTick == 0 || Stopwatch.GetElapsedTime(previousTick, now).TotalSeconds >= 1d / target - .002)
                {
                    previousTick = now;
                    previousSubmitted++;
                }
                if (Advance(clock, now, target, false, out _)) submitted++;
            }
            int expected = seconds * Math.Min(display, target);
            if (Math.Abs(submitted - expected) > 1)
                throw new InvalidOperationException($"{display} Hz at {target} FPS submitted {submitted} frames in {seconds}s, expected {expected}.");
            results.Add(new { DisplayHz = display, TargetFps = target, PreviousFps = previousSubmitted / (float)seconds, PresentedFps = submitted / (float)seconds });
        }

        object resumed = Activator.CreateInstance(clockType)!;
        if (!Advance(resumed, start, 60, false, out var first) || first != TimeSpan.Zero
            || !Advance(resumed, start * 4, 60, false, out var afterStall) || afterStall != TimeSpan.FromSeconds(3)
            || Advance(resumed, start * 4 + 1, 60, false, out _)
            || !Advance(resumed, start * 4 + 2, 60, true, out _)
            || Advance(resumed, start * 4 + 3, 60, false, out _)
            || !Advance(resumed, start * 4 + 4, 120, false, out _)
            || Advance(resumed, start * 4 + 5, 120, false, out _))
            throw new InvalidOperationException("The presentation clock did not drop missed frames or schedule surface replacements and rate changes correctly.");
        return results;

        bool Advance(object state, long now, double rate, bool force, out TimeSpan elapsed)
        {
            object?[] args = [now, rate, force, null];
            bool due = (bool)tryAdvance.Invoke(state, args)!;
            elapsed = (TimeSpan)args[3]!;
            return due;
        }
    }
}
