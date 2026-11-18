using System;
using System.Diagnostics;

namespace AnimatedWin2dControls.Interop;

/// <summary>Limits presentation to a stable clock when display callbacks and the requested rate differ.</summary>
internal struct PresentationFrameClock
{
    private long _last;
    private double _interval;
    private double _deadline;

    internal bool TryAdvance(long now, double frameRate, bool forceFrame, out TimeSpan elapsed)
    {
        double rate = double.IsFinite(frameRate) && frameRate > 0 ? frameRate : 60;
        double interval = Stopwatch.Frequency / rate;
        if (forceFrame || _last == 0 || interval != _interval)
        {
            _interval = interval;
            _deadline = now + interval;
        }
        else
        {
            // A small allowance absorbs composition callback jitter without shifting future deadlines.
            double allowance = Math.Min(Stopwatch.Frequency * .001, interval * .12);
            if (now + allowance < _deadline)
            {
                elapsed = default;
                return false;
            }

            // Drop missed deadlines so a delayed callback can never produce catch-up frames.
            double periods = Math.Max(1, Math.Floor((now - _deadline) / interval) + 1);
            _deadline += interval * periods;
        }

        elapsed = _last == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_last, now);
        _last = now;
        return true;
    }
}
