using System;
using System.Threading.Tasks;
using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel.Controls;

public partial class DspSettingsViewModel
{
    public bool QuickPresetEditable => CrossfeedEditable && WidthEditable;
    public bool StereoImagePresetEditable => QuickPresetEditable && StereoSupported && !Mono;
    public bool GraphicEqPresetEditable => QuickPresetEditable && _lastState is { Channels: <= 2 };
    public bool ClubPresetEditable => StereoImagePresetEditable && GraphicEqPresetEditable;
    public bool HasSceneEqualizer => AppSettings.SceneEqualizer != SceneEqualizer.Personal;
    public string SceneEqualizerMessage => HasSceneEqualizer
        ? string.Format(ToolUtils.GetString("DspSceneEqualizerMessage"), ToolUtils.GetString(AppSettings.SceneEqualizer switch
        {
            SceneEqualizer.ConcertHall => "DspQuickPresetConcertHall",
            SceneEqualizer.ClearVocals => "DspQuickPresetClearVocals",
            SceneEqualizer.WarmBass => "DspQuickPresetWarmBass",
            SceneEqualizer.Radio => "DspQuickPresetRadio",
            SceneEqualizer.Club => "DspQuickPresetClub",
            SceneEqualizer.Bright => "DspQuickPresetBright",
            _ => "DspQuickPresetCustom"
        }))
        : "";

    public int QuickPresetIndex
    {
        get => DspQuickPreset.GetIndex(AppSettings.Dsp, AppSettings.SceneEqualizer);
        set
        {
            if (_syncing || !_loaded || !QuickPresetEditable || (uint)value >= DspQuickPreset.CustomIndex
                || (DspQuickPreset.RequiresStereo(value) && !StereoImagePresetEditable)
                || (DspQuickPreset.RequiresGraphicEq(value) && !GraphicEqPresetEditable)
                || value == QuickPresetIndex)
                return;

            AppSettings.Dsp = DspQuickPreset.Apply(AppSettings.Dsp, value);
            AppSettings.SceneEqualizer = DspQuickPreset.GetEqualizer(value);
            LoadValues();
            _dirty = true;
            _commitTimer.Stop();
            _ = CommitAsync();
        }
    }

    private void OnSceneEqualizerChanged(object? sender, EventArgs args)
    {
        bool syncing = _syncing;
        _syncing = true;
        OnPropertyChanged(nameof(QuickPresetIndex));
        _syncing = syncing;
    }

    [RelayCommand]
    private async Task UsePersonalEqualizerAsync()
    {
        if (!HasSceneEqualizer && !_dirty) return;
        AppSettings.SceneEqualizer = SceneEqualizer.Personal;
        OnPropertyChanged(nameof(HasSceneEqualizer));
        OnPropertyChanged(nameof(SceneEqualizerMessage));
        _dirty = true;
        await CommitAsync();
    }

    internal void RefreshSceneEqualizer()
    {
        OnPropertyChanged(nameof(HasSceneEqualizer));
        OnPropertyChanged(nameof(SceneEqualizerMessage));
    }

    public bool ConcertHallEditable => EffectsActive && _lastState is { Channels: <= 2 };
    public bool ConcertHallEnabled
    {
        get => field;
        set
        {
            if (SetProperty(ref field, value)) SettingChanged(LicenseFeature.None);
        }
    }
}
