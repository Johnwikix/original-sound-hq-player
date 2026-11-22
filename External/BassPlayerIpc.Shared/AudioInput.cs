namespace BassPlayerIpc.Shared;

/// <summary>Half-open sample frame interval in the original audio stream.</summary>
public readonly record struct AudioSampleRange(long StartSample, long EndSample, int SampleRate)
{
    public long FrameCount => EndSample - StartSample;
    public long DurationMs => (long)(((Int128)FrameCount * 1000 + SampleRate - 1) / SampleRate);

    public void Validate()
    {
        if (StartSample < 0 || EndSample <= StartSample || SampleRate < 1000)
            throw new ArgumentException("Invalid audio sample range.");
    }

    public long SampleAt(long positionMs)
    {
        if (positionMs <= 0) return StartSample;
        if (positionMs >= DurationMs) return EndSample;
        return StartSample + (long)((Int128)positionMs * SampleRate / 1000);
    }
}

/// <summary>Physical audio path and optional source sample interval.</summary>
public readonly record struct AudioInput(string Path, AudioSampleRange? SampleRange = null)
{
    public bool IsImmediatelyFollowedBy(AudioInput next) =>
        SampleRange is { } currentRange && next.SampleRange is { } nextRange
        && currentRange.SampleRate == nextRange.SampleRate && currentRange.EndSample == nextRange.StartSample
        && string.Equals(Path, next.Path, StringComparison.OrdinalIgnoreCase);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Path) || Path.Contains('\0'))
            throw new ArgumentException("Invalid audio path.");
        if (SampleRange is { } range)
        {
            if (!System.IO.Path.IsPathFullyQualified(Path))
                throw new ArgumentException("A sample range requires an absolute local path.");
            range.Validate();
        }
    }
}
