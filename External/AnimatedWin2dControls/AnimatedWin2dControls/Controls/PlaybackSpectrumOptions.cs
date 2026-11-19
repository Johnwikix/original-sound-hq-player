using System;

namespace AnimatedWin2dControls.Controls;

public enum SpectrumPlacement { Top, Bottom, AroundArtwork }
public enum SpectrumStyle { Curve, Bars }
public enum SpectrumColorMode { AdaptiveColor, AdaptiveGray, Custom }

public readonly record struct PlaybackSpectrumOptions
{
    public const int MaximumCount = 1024;
    public const int MaximumDelayMs = 2000;

    public PlaybackSpectrumOptions() { }

    public bool Enabled { get; init; }
    public SpectrumPlacement Placement { get; init; } = SpectrumPlacement.Bottom;
    public SpectrumStyle Style { get; init; }
    public SpectrumColorMode ColorMode { get; init; } = SpectrumColorMode.AdaptiveGray;
    public uint CustomArgb { get; init; } = 0xffffffff;
    public int Opacity { get; init; } = 100;
    public int Count { get; init; } = 32;
    public int Sensitivity { get; init; } = 100;
    public int DelayMs { get; init; }
    public bool Breathing { get; init; }
    public int BreathingIntensity { get; init; } = 80;
    public bool Glow { get; init; }
    public bool Parallax { get; init; }

    public PlaybackSpectrumOptions Normalize() => this with
    {
        Placement = Enum.IsDefined(Placement) ? Placement : SpectrumPlacement.Bottom,
        Style = Enum.IsDefined(Style) ? Style : SpectrumStyle.Curve,
        ColorMode = Enum.IsDefined(ColorMode) ? ColorMode : SpectrumColorMode.AdaptiveGray,
        Opacity = Math.Clamp(Opacity, 0, 100),
        Count = Math.Clamp(Count, 1, MaximumCount),
        Sensitivity = Math.Clamp(Sensitivity, 50, 500),
        DelayMs = Math.Clamp(DelayMs, 0, MaximumDelayMs),
        BreathingIntensity = Math.Clamp(BreathingIntensity, 0, 200)
    };
}
