using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>全局音频偏好；设备校正、曲线预设、EQ 库和播放进度各自独立保存。</summary>
public sealed record AudioPreferences
{
    public DspSettings Dsp { get; init; } = new();
    public string OutputMode { get; init; } = "DirectSound";
    public int Latency { get; init; } = 300;
    public int BassOutputDeviceId { get; init; } = -1;
    public string? WasapiEndpointId { get; init; }
    public int BassASIODeviceId { get; init; }
    public string DeviceFriendlyName { get; init; } = "";
    public bool IsFadeEnabled { get; init; }
    public bool IsDopEnabled { get; init; }
    public bool ExperimentalSurround51 { get; init; }
    public bool ExperimentalAtmosPassthrough { get; init; }
    public int DsdGain { get; init; } = 6;
    public int DsdPcmFreq { get; init; } = 88200;

    /// <summary>按播放端范围归一化旧值，稳定端点 ID 保持原样。</summary>
    public AudioPreferences Sanitize()
    {
        if (Dsp == null) throw new ArgumentException("Missing DSP settings.");
        if (WasapiEndpointId is { } id && (Encoding.UTF8.GetByteCount(id) > 256 || id.Contains('\0')))
            throw new ArgumentException("Invalid WASAPI endpoint ID.");
        if (DeviceFriendlyName == null || DeviceFriendlyName.Length > 1024)
            throw new ArgumentException("Invalid output device name.");
        return this with
        {
            Dsp = Dsp.Sanitize(),
            OutputMode = OutputMode is "DirectSound" or "WasapiShared" or "WasapiExclusiveEvent" or "WasapiExclusivePush" or "ASIO"
                ? OutputMode : "DirectSound",
            Latency = Math.Clamp(Latency, 10, 2000),
            BassOutputDeviceId = Math.Max(-1, BassOutputDeviceId),
            BassASIODeviceId = Math.Max(0, BassASIODeviceId),
            DsdGain = Math.Clamp(DsdGain, -24, 24),
            DsdPcmFreq = Math.Clamp(DsdPcmFreq, 8000, 768000)
        };
    }
}
