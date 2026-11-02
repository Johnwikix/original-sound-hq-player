using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public sealed partial class SettingsSnapshotFactory
{
    /// <summary>在 UI 线程捕获原始音频偏好，不将许可覆盖写入持久化快照。</summary>
    // 冷路径生成一次不可变快照；后台序列化不再读取 ViewModel。
    public AudioPreferences CaptureAudio() => new()
    {
        Dsp = AppSettings.Dsp,
        OutputMode = AppSettings.OutputMode,
        Latency = state.Preferences.Latency,
        BassOutputDeviceId = AppSettings.BassOutputDeviceId,
        WasapiEndpointId = AppSettings.WasapiEndpointId,
        BassASIODeviceId = AppSettings.BassASIODeviceId,
        DeviceFriendlyName = AppSettings.DeviceName,
        IsFadeEnabled = state.Preferences.IsFadeEnabled,
        SpectralEnhancement = (SpectralEnhancementMode)state.Preferences.Audio.SpectralEnhancement,
        IsDopEnabled = state.Preferences.Audio.IsDopEnabled,
        ExperimentalSurround51 = state.Preferences.Audio.ExperimentalSurround51,
        ExperimentalAtmosPassthrough = state.Preferences.Audio.ExperimentalAtmosPassthrough,
        AtmosEndpointId = state.Preferences.Audio.AtmosEndpointId,
        DsdGain = state.Preferences.DsdGain,
        DsdPcmFreq = state.Preferences.DsdPcmFreq
    };

}
