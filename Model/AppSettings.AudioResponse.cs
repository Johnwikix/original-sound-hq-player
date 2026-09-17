using System;
using System.Threading;
using BassPlayerIpc.Shared;

namespace WinUIMusicPlayer.Model;

public static partial class AppSettings
{
    public static event EventHandler? AudioResponseChanged;

    public static DspSettings Dsp
    {
        get => field;
        set
        {
            if (field == value) return;
            field = value;
            AudioResponseChanged?.Invoke(null, EventArgs.Empty);
        }
    } = new();

    public static DeviceCorrections DeviceCorrections
    {
        get => field;
        set
        {
            if (ReferenceEquals(field, value)) return;
            // Mode changes and removing the active saved binding end its live override.
            if (field.Enabled != value.Enabled || (_liveCorrection is { } live
                && field.Find(live.Correction.DeviceId) != null && value.Find(live.Correction.DeviceId) == null))
                _liveCorrection = null;
            field = value;
            AudioResponseChanged?.Invoke(null, EventArgs.Empty);
        }
    } = new();

    private sealed record LiveCorrection(DeviceCorrection Correction, long Generation, bool FollowOutputs = false,
        string SourcePresetName = "");
    private static volatile LiveCorrection? _liveCorrection;

    /// <summary>自定义草稿跨输出实时生效，只保存在本进程内。</summary>
    public static bool IsCustomCorrectionDraft => DeviceCorrections.Enabled && _liveCorrection is { FollowOutputs: true };
    /// <summary>草稿原先选中的预设，仅用于恢复编辑器的“重新应用”目标。</summary>
    public static string CustomCorrectionPresetName => IsCustomCorrectionDraft ? _liveCorrection?.SourcePresetName ?? "" : "";

    /// <summary>替换会话草稿；不写入全局曲线或设备绑定。</summary>
    public static void SetCustomCorrectionDraft(DspSettings settings, string sourcePresetName = "")
    {
        _liveCorrection = new(new DeviceCorrection { Settings = settings }, 0, FollowOutputs: true, SourcePresetName: sourcePresetName);
        AudioResponseChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>无输出时选定的预设只绑定首次实际输出；自定义草稿则始终跟随输出。</summary>
    public static void BindPendingCorrectionOutput(string outputId, long generation)
    {
        if (string.IsNullOrEmpty(outputId)) return;
        var pending = _liveCorrection;
        if (pending is not { FollowOutputs: false, Correction.DeviceId.Length: 0 }) return;
        var bound = pending with { Correction = pending.Correction with { DeviceId = outputId }, Generation = generation };
        // 监听线程不能用迟到的输出覆盖 UI 刚写入的草稿或开关重置。
        Interlocked.CompareExchange(ref _liveCorrection, bound, pending);
    }

    /// <summary>Session-only correction; never stored in the device binding collection.</summary>
    public static void SetLiveCorrection(string outputId, long generation, DspSettings settings)
    {
        _liveCorrection = new(new DeviceCorrection { DeviceId = outputId, Settings = settings }, generation);
        AudioResponseChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool TryGetLiveCorrection(string? outputId, long generation, out DspSettings settings)
    {
        var live = _liveCorrection;
        if (DeviceCorrections.Enabled && live != null && (live.FollowOutputs
            || (live.Generation == generation && string.Equals(live.Correction.DeviceId, outputId ?? "", StringComparison.OrdinalIgnoreCase))))
        {
            settings = live.Correction.Apply(Dsp).Sanitize();
            return true;
        }
        settings = Dsp;
        return false;
    }

    // The playback publisher and both response views resolve the same live/saved correction.
    public static DspSettings ResolveResponseSettings(string? outputId, long generation = 0) =>
        TryGetLiveCorrection(outputId, generation, out var live) ? live : DeviceCorrections.Resolve(Dsp, outputId).Sanitize();
}
