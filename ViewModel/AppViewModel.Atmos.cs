using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    public ObservableCollection<BassOutputDevice> AtmosDevices { get; } = new();
    public string AtmosEndpointId
    {
        get => field;
        set
        {
            if (value == null) return;
            if (!SetProperty(ref field, value)) return;
            OnPropertyChanged(nameof(SelectedAtmosDevice));
            if (IsInitialized)
            {
                _ = _musicDatabaseService.SaveSettingAsync();
                AppSettings.OnOutputSettingsUpdated();
            }
        }
    } = "";

    public BassOutputDevice? SelectedAtmosDevice
    {
        get
        {
            foreach (var device in AtmosDevices)
                if (string.Equals(device.EndpointId, AtmosEndpointId, StringComparison.OrdinalIgnoreCase)) return device;
            return null;
        }
        set
        {
            if (!_isLoadingDevices && value != null) AtmosEndpointId = value.EndpointId ?? "";
        }
    }

    public string AtmosStatusText { get => State.Output.AtmosStatusText; private set => State.Output.AtmosStatusText = value; }

    public void ApplyAtmosState(DspState state)
    {
        string text = ToolUtils.GetString(state.AtmosStatus switch
        {
            AtmosPlaybackStatus.Active => "AtmosActive",
            AtmosPlaybackStatus.Ready => "AtmosReady",
            AtmosPlaybackStatus.PcmFallback => "AtmosFallback",
            AtmosPlaybackStatus.Stopped => "AtmosStopped",
            AtmosPlaybackStatus.Off => "AtmosOff",
            _ => "AtmosWaiting"
        });
        string reason = AtmosText.Failure(state.AtmosReason);
        if (reason.Length != 0) text += " " + reason;
        if (AtmosStatusText == text) return;
        AtmosStatusText = text;
    }

    private void RefreshAtmosDevices()
    {
        AtmosDevices.Clear();
        AtmosDevices.Add(new BassOutputDevice { EndpointId = "", Name = ToolUtils.GetString("AtmosCurrentDevice") });
        bool found = string.IsNullOrEmpty(AtmosEndpointId);
        foreach (var device in BassOutputDevices)
        {
            if (device.OutputMode != "WasapiShared" || string.IsNullOrEmpty(device.EndpointId)) continue;
            AtmosDevices.Add(device);
            found |= string.Equals(device.EndpointId, AtmosEndpointId, StringComparison.OrdinalIgnoreCase);
        }
        if (!found)
            AtmosDevices.Add(new BassOutputDevice { EndpointId = AtmosEndpointId, Name = ToolUtils.GetString("AtmosSavedDeviceUnavailable") });
        OnPropertyChanged(nameof(SelectedAtmosDevice));
    }

    [RelayCommand]
    private void UseOrdinaryPlayback() => ExperimentalAtmosPassthrough = false;
}
