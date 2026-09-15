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
            field = value;
            AudioResponseChanged?.Invoke(null, EventArgs.Empty);
        }
    } = new();

    // Both editors use the same effective settings, including unbound-device bypass.
    public static DspSettings ResolveResponseSettings(string? outputId) =>
        DeviceCorrections.Resolve(Dsp, outputId).Sanitize();
}
