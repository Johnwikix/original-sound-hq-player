using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.ViewModel.Controls;

public partial class DspSettingsViewModel
{
    private static readonly int[] PcmTargetRates = [44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000, 705600, 768000];

    /// <summary>获取可重建当前位置的 PCM 配置可用状态。</summary>
    public bool PcmConfigurationEditable => EffectsActive && (_lastState?.CanChangePlaybackRate ?? false);

    /// <summary>获取或设置普通 PCM 重采样策略。</summary>
    public int ResampleModeIndex
    {
        get => field;
        set { if ((uint)value <= 2 && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    /// <summary>获取或设置目标采样率的选项索引。</summary>
    public int ResampleTargetIndex
    {
        get => field;
        set { if ((uint)value < PcmTargetRates.Length && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    } = 5;

    /// <summary>获取或设置最终 True Peak 前瞻保护开关。</summary>
    public bool TruePeakLimiterEnabled
    {
        get => field;
        set { if (SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    /// <summary>获取或设置峰值上限，单位 dBTP。</summary>
    public double TruePeakCeilingDb
    {
        get => field;
        set { if (double.IsFinite(value) && SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    } = -1;

    /// <summary>获取或设置整数输出的可选 TPDF 抖动。</summary>
    public bool DitherEnabled
    {
        get => field;
        set { if (SetProperty(ref field, value)) SettingChanged(LicenseFeature.None); }
    }

    /// <summary>获取或设置耳机交叉馈送的截止频率，单位 Hz。</summary>
    public double CrossfeedCutoffHz
    {
        get => field;
        set { if (double.IsFinite(value) && SetProperty(ref field, value)) SettingChanged(LicenseFeature.Crossfeed); }
    } = 700;

    /// <summary>获取或设置交叉馈送强度，单位百分比；档位仅作为快捷预设。</summary>
    public double CrossfeedAmount
    {
        get => field;
        set
        {
            if (!double.IsFinite(value) || !SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(CrossfeedIndex));
            SettingChanged(LicenseFeature.Crossfeed);
        }
    }

    private void LoadPcmValues(DspSettings settings, bool effectsActive)
    {
        ResampleModeIndex = (int)settings.ResampleMode;
        ResampleTargetIndex = System.Array.IndexOf(PcmTargetRates, settings.ResampleTargetRate);
        TruePeakLimiterEnabled = effectsActive && settings.TruePeakLimiterEnabled;
        TruePeakCeilingDb = settings.TruePeakCeilingDb;
        DitherEnabled = effectsActive && settings.DitherEnabled;
        CrossfeedCutoffHz = settings.CrossfeedCutoffHz;
        CrossfeedAmount = effectsActive ? settings.Crossfeed * 100 : 0;
    }
}
