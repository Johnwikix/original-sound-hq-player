using BassPlayerIpc.Shared;

namespace WinUIMusicPlayer.Model;

public partial class SaveSettings
{
    // 仅用于旧版配置迁移；新的音频偏好由 AudioSettingsStore 承载。
    public AudioPreferences ReadLegacyAudioPreferences() => new()
    {
        Dsp = Dsp ?? new(),
        OutputMode = OutputMode ?? "DirectSound",
        Latency = Latency ?? 300,
        BassOutputDeviceId = BassOutputDeviceId ?? -1,
        WasapiEndpointId = WasapiEndpointId,
        BassASIODeviceId = BassASIODeviceId ?? 0,
        DeviceFriendlyName = DeviceFriendlyName ?? "",
        IsFadeEnabled = IsFadeEnabled ?? false,
        IsDopEnabled = IsDopEnabled ?? false,
        ExperimentalSurround51 = ExperimentalSurround51 ?? false,
        ExperimentalAtmosPassthrough = ExperimentalAtmosPassthrough ?? false,
        DsdGain = DsdGain ?? 6,
        DsdPcmFreq = DsdPcmFreq ?? 88200
    };

    public void ClearLegacyAudioPreferences()
    {
        Dsp = null;
        OutputMode = null;
        Latency = null;
        BassOutputDeviceId = null;
        WasapiEndpointId = null;
        BassASIODeviceId = null;
        DeviceFriendlyName = null;
        IsFadeEnabled = null;
        IsDopEnabled = null;
        ExperimentalSurround51 = null;
        ExperimentalAtmosPassthrough = null;
        DsdGain = null;
        DsdPcmFreq = null;
    }

    public bool HasLegacyAudioPreferences() => Dsp != null || OutputMode != null || Latency != null
        || BassOutputDeviceId != null || WasapiEndpointId != null || BassASIODeviceId != null
        || DeviceFriendlyName != null || IsFadeEnabled != null || IsDopEnabled != null
        || ExperimentalSurround51 != null || ExperimentalAtmosPassthrough != null || DsdGain != null || DsdPcmFreq != null;
}
