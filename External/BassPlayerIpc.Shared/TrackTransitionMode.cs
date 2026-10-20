namespace BassPlayerIpc.Shared;

/// <summary>One playback preference selects the next-track policy; independent of the DSP master switch.</summary>
public enum TrackTransitionMode : byte
{
    Normal,
    Gapless,
    Crossfade,
    AutoMix
}
