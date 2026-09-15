using System;
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

    private sealed record LiveCorrection(DeviceCorrection Correction, long Generation);
    private static volatile LiveCorrection? _liveCorrection;

    /// <summary>Session-only correction; never stored in the device binding collection.</summary>
    public static void SetLiveCorrection(string outputId, long generation, DspSettings settings)
    {
        _liveCorrection = new(new DeviceCorrection { DeviceId = outputId, Settings = settings }, generation);
        AudioResponseChanged?.Invoke(null, EventArgs.Empty);
    }

    public static bool TryGetLiveCorrection(string? outputId, long generation, out DspSettings settings)
    {
        var live = _liveCorrection;
        if (DeviceCorrections.Enabled && live != null && live.Generation == generation
            && string.Equals(live.Correction.DeviceId, outputId, StringComparison.OrdinalIgnoreCase))
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
