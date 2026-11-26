using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Model;

/// <summary>Defines the PCM controls and optional equalizer curve applied together by a quick preset.</summary>
internal readonly record struct DspQuickPreset(
    double StereoWidth = 1,
    bool CompressorEnabled = false,
    double CompressorThresholdDb = -18,
    double CompressorRatio = 4,
    bool ConcertHallEnabled = false,
    SceneEqualizer Equalizer = SceneEqualizer.Personal,
    bool RequiresStereoImage = false)
{
    private static readonly DspQuickPreset[] Presets =
    [
        new(StereoWidth: 1), // Original
        new(StereoWidth: 1.2, RequiresStereoImage: true), // Wide
        new(StereoWidth: 1.3, ConcertHallEnabled: true, Equalizer: SceneEqualizer.ConcertHall),
        new(Equalizer: SceneEqualizer.ClearVocals),
        new(Equalizer: SceneEqualizer.WarmBass),
        new(CompressorEnabled: true, CompressorThresholdDb: -30, CompressorRatio: 5, Equalizer: SceneEqualizer.Radio),
        new(StereoWidth: 1.35, CompressorEnabled: true, CompressorThresholdDb: -14, CompressorRatio: 2.5,
            Equalizer: SceneEqualizer.Club, RequiresStereoImage: true),
        new(Equalizer: SceneEqualizer.Bright)
    ];

    internal static int CustomIndex => Presets.Length;
    internal static bool RequiresStereo(int index) => Presets[index].RequiresStereoImage;
    internal static bool RequiresGraphicEq(int index) => GetEqualizer(index) != SceneEqualizer.Personal;
    internal static SceneEqualizer GetEqualizer(int index) => Presets[index].Equalizer;

    internal static int GetIndex(DspSettings settings, SceneEqualizer equalizer)
    {
        for (int i = 0; i < Presets.Length; i++)
        {
            if (Presets[i].Matches(settings, equalizer))
                return i;
        }
        return CustomIndex;
    }

    internal static DspSettings Apply(DspSettings settings, int index)
    {
        DspQuickPreset preset = Presets[index];
        // Keep the detailed parameters of effects the scene turns off.
        return settings with
        {
            StereoWidth = preset.StereoWidth,
            Crossfeed = 0,
            CompressorEnabled = preset.CompressorEnabled,
            CompressorThresholdDb = preset.CompressorEnabled ? preset.CompressorThresholdDb : settings.CompressorThresholdDb,
            CompressorRatio = preset.CompressorEnabled ? preset.CompressorRatio : settings.CompressorRatio,
            CompressorMakeupDb = preset.CompressorEnabled ? 0 : settings.CompressorMakeupDb,
            ConcertHallEnabled = preset.ConcertHallEnabled
        };
    }

    private bool Matches(DspSettings settings, SceneEqualizer equalizer) =>
        Equalizer == equalizer
        && settings.ConcertHallEnabled == ConcertHallEnabled
        && settings.StereoWidth == StereoWidth
        && settings.Crossfeed == 0
        && settings.CompressorEnabled == CompressorEnabled
        && (!CompressorEnabled || (settings.CompressorThresholdDb == CompressorThresholdDb
            && settings.CompressorRatio == CompressorRatio && settings.CompressorMakeupDb == 0));
}

internal static class SceneEqualizerCurves
{
    // Cut competing frequencies to shape the sound without boosting peaks before other DSP.
    private static readonly EqBand[] ConcertHall = Build([-2, -1, 0, 0, -0.5, 0, 0, -0.5, -1.5, -2.5]);
    private static readonly EqBand[] ClearVocals = Build([-4, -3.5, -2.5, -1.5, -0.5, 0, 0, 0, -0.5, -1.5]);
    private static readonly EqBand[] WarmBass = Build([-0.5, 0, 0, -0.5, -1, -1.5, -2, -2.5, -3, -3.5]);

    private static readonly EqBand[] Radio = Build([-12, -12, -12, -10, -3, 0, -1, -10, -12, -12], q: 0.7);
    private static readonly EqBand[] Club = Build([0, 0, -1, -5, -7, -6, -4, -2, -1, -1], q: 0.85);
    private static readonly EqBand[] Bright = Build([-6, -5, -4, -3, -2, -2, -1, 0, 0, 0], q: 0.75);

    private static EqBand[] Build(double[] gains, double q = EqPreset.DefaultQ) =>
        EqualizerHelper.CreatePreset(string.Empty, gains, q).Bands.ToArray();

    internal static EqBand[]? GetBands(SceneEqualizer equalizer) => equalizer switch
    {
        SceneEqualizer.ConcertHall => ConcertHall,
        SceneEqualizer.ClearVocals => ClearVocals,
        SceneEqualizer.WarmBass => WarmBass,
        SceneEqualizer.Radio => Radio,
        SceneEqualizer.Club => Club,
        SceneEqualizer.Bright => Bright,
        _ => null
    };
}
