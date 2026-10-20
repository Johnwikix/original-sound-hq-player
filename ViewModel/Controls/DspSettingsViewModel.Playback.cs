using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Controls;

public partial class DspSettingsViewModel
{
    private static readonly double[] PlaybackRates = [0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 5];

    public bool PlaybackRateEditable => EffectsActive && (_lastState?.CanChangePlaybackRate ?? false);

    public double PlaybackRate
    {
        get => field;
        set
        {
            if (!double.IsFinite(value) || !SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(PlaybackRateIndex));
            OnPropertyChanged(nameof(PlaybackRateText));
            SettingChanged(LicenseFeature.None);
        }
    } = AppSettings.Dsp.PlaybackRate;

    public int PlaybackRateIndex
    {
        get => System.Array.IndexOf(PlaybackRates, PlaybackRate);
        set
        {
            // Loading/x:Bind can publish a transient selection before saved values are restored.
            if (_syncing || !_loaded) return;
            // A legacy custom rate has no selected item. Ignore selection clearing during binding updates.
            if ((uint)value < (uint)PlaybackRates.Length) PlaybackRate = PlaybackRates[value];
        }
    }

    public string PlaybackRateText => PlaybackRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "×";

    public bool CompressorEnabled
    {
        get => field;
        set { if (SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    public double CompressorThresholdDb
    {
        get => field;
        set { if (double.IsFinite(value) && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    public double CompressorRatio
    {
        get => field;
        set { if (double.IsFinite(value) && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    public double CompressorMakeupDb
    {
        get => field;
        set { if (double.IsFinite(value) && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }
}
