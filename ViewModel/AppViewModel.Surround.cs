using BassPlayerIpc.Shared;
using CommunityToolkit.Mvvm.Input;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.ViewModel;

public partial class AppViewModel
{
    public string SurroundStatusText { get => State.Output.SurroundStatusText; private set => State.Output.SurroundStatusText = value; }

    public void ApplySurroundState(DspState state)
    {
        string text = ToolUtils.GetString(state.SurroundStatus switch
        {
            SurroundPlaybackStatus.Active => "SurroundActive",
            SurroundPlaybackStatus.Ready => "SurroundReady",
            SurroundPlaybackStatus.Fallback => "SurroundFallback",
            SurroundPlaybackStatus.Stopped => "SurroundStopped",
            SurroundPlaybackStatus.Off => "SurroundOff",
            _ => "SurroundWaiting"
        });
        if (SurroundStatusText == text) return;
        SurroundStatusText = text;
    }

    [RelayCommand]
    private void UseOrdinarySurroundPlayback() => ExperimentalSurround51 = false;
}
