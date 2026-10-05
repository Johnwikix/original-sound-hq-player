using System;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// Reserved audio-reactive input for wallpaper renderers. Values are normalized to 0..1;
/// the current Folia implementation deliberately ignores them and renders the standard
/// motion curve so enabling wallpaper lyrics does not depend on an analyser being ready.
/// </summary>
public readonly record struct DesktopLyricsAudioSpectrum(
    float Bass,
    float LowMid,
    float Mid,
    float Vocal,
    float Treble,
    float Overall)
{
    public static DesktopLyricsAudioSpectrum Empty => default;

    public DesktopLyricsAudioSpectrum Clamped => new(
        Clamp(Bass),
        Clamp(LowMid),
        Clamp(Mid),
        Clamp(Vocal),
        Clamp(Treble),
        Clamp(Overall));

    private static float Clamp(float value) => Math.Clamp(value, 0, 1);
}

/// <summary>Optional capability implemented by renderers that can accept spectrum data.</summary>
public interface IDesktopLyricsAudioSpectrumSink
{
    void SetAudioSpectrum(DesktopLyricsAudioSpectrum spectrum);
}
