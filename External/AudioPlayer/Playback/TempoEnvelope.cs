namespace AudioPlayer.Playback;

/// <summary>Immutable output-frame clock shared by the decoder and progress calculation.</summary>
internal readonly record struct TempoEnvelope(double InitialRate, long HoldFrames, long RampFrames)
{
    internal bool IsActive => InitialRate > 0 && RampFrames > 0;
    internal long EndFrames => HoldFrames + RampFrames;

    internal double SourceFrames(double outputFrames)
    {
        if (outputFrames <= HoldFrames) return outputFrames * InitialRate;
        double ramp = Math.Min(RampFrames, outputFrames - HoldFrames);
        double t = ramp / RampFrames;
        double integral = InitialRate * t + (1 - InitialRate) * (t * t * t - 0.5 * t * t * t * t);
        return HoldFrames * InitialRate + RampFrames * integral + Math.Max(0, outputFrames - EndFrames);
    }

    internal double RateAt(double outputFrames)
    {
        double t = Math.Clamp((outputFrames - HoldFrames) / RampFrames, 0, 1);
        return InitialRate + (1 - InitialRate) * t * t * (3 - 2 * t);
    }

    internal double OutputFrames(double sourceFrames)
    {
        double held = HoldFrames * InitialRate;
        if (sourceFrames <= held) return sourceFrames / InitialRate;
        double rampEnd = held + RampFrames * (InitialRate + 1) * 0.5;
        if (sourceFrames >= rampEnd) return EndFrames + sourceFrames - rampEnd;
        double low = HoldFrames, high = EndFrames;
        for (int i = 0; i < 32; i++)
        {
            double middle = (low + high) * 0.5;
            if (SourceFrames(middle) < sourceFrames) low = middle;
            else high = middle;
        }
        return (low + high) * 0.5;
    }
}
