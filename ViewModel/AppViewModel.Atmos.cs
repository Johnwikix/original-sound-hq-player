using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    public ObservableCollection<BassOutputDevice> AtmosDevices => State.Output.AtmosDevices;
    public string AtmosEndpointId
    {
        get => State.Preferences.Audio.AtmosEndpointId;
        set
        {
            if (value == null) return;
            if (State.Preferences.Audio.AtmosEndpointId == value) return;
            State.Preferences.Audio.AtmosEndpointId = value;
        }
    }

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
            if (!State.Output.IsLoadingDevices && value != null) AtmosEndpointId = value.EndpointId ?? "";
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

    [RelayCommand]
    private void UseOrdinaryPlayback() => ExperimentalAtmosPassthrough = false;
}
