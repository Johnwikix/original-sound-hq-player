using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class IpcService
{
    private long _lastAtmosFailureSequence;
    private int _atmosUiQueued;

    private void SynchronizeAtmosState(DspStateSnapshot snapshot)
    {
        var state = snapshot.State;
        if (state.AtmosFailureSequence > _lastAtmosFailureSequence)
        {
            _lastAtmosFailureSequence = state.AtmosFailureSequence;
            try
            {
                _systemNotifications.SendNotification(ToolUtils.GetString("AtmosNotificationTitle"),
                    ToolUtils.GetString(state.AtmosFailureStopped ? "AtmosStopped" : "AtmosFallback") + Environment.NewLine
                    + AtmosText.Failure(state.LastAtmosFailure), "atmos-" + state.LastAtmosFailure + "-" + state.AtmosFailureStopped);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "Atmos notification preparation failed"); }
        }
        if (Interlocked.Exchange(ref _atmosUiQueued, 1) != 0) return;
        if (App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        {
            Interlocked.Exchange(ref _atmosUiQueued, 0);
            if (Volatile.Read(ref _disposed) == 0 && CurrentDspState is { } latest)
                AppViewModel.ApplyAtmosState(latest.State);
        }) != true) Interlocked.Exchange(ref _atmosUiQueued, 0);
    }
}
