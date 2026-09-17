using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using System;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public partial class IpcService
{
    /// <summary>输出身份变化时重推会话校正；不依赖任何 UI 订阅者，普通响度通知不重复发送。</summary>
    private void SynchronizeCorrectionOutput(DspState? previous, DspState current)
    {
        if (previous?.OutputDeviceId == current.OutputDeviceId
            && previous?.OutputGeneration == current.OutputGeneration) return;

        AppSettings.BindPendingCorrectionOutput(current.OutputDeviceId, current.OutputGeneration);
        try { PublishLiveCorrection(); }
        catch (Exception ex) { _logger.LogWarning(ex, "Live correction output synchronization failed"); }
    }

    private void PublishLiveCorrection()
    {
        if (CurrentDspState?.State is not { OutputDeviceId.Length: > 0 } current) return;
        // 首播通知和 UI 应用预设可能交错；每次发送都补做待定预设的输出绑定。
        AppSettings.BindPendingCorrectionOutput(current.OutputDeviceId, current.OutputGeneration);
        if (AppSettings.TryGetLiveCorrection(current.OutputDeviceId, current.OutputGeneration, out var settings))
            PreviewDsp(settings, current.OutputDeviceId, current.OutputGeneration);
    }
}
