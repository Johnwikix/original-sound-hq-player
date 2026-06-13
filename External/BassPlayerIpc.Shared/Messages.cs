namespace BassPlayerIpc.Shared;

public struct PlayRequest
{
    public string? Url;
}

public struct SetMusicUrlRequest
{
    public string? Url;
}

public struct ChangePositionRequest
{
    public long PositionMs;
}

public struct ChangeVolumeRequest
{
    public double Volume;
}

public struct IpcSetting
{
    public string? OutputMode;
    public int BassOutputDeviceId;
    public int BassASIODeviceId;
    public int Latency;
    public bool IsDopEnabled;
    public int DsdGain;
    public int DsdPcmFreq;
    public bool IsEqualizerEnabled;
    public float Volume;
    public bool IsSettingChanged;
    public bool IsFadeEnabled;
}

public struct SetEqualizerGainRequest
{
    public byte BandIndex;
    public float Gain;
}

public struct UpdateEqRequest
{
    public float Band0;
    public float Band1;
    public float Band2;
    public float Band3;
    public float Band4;
    public float Band5;
    public float Band6;
    public float Band7;
    public float Band8;
    public float Band9;
}

// Response payloads

public struct FailedResponse
{
    public ErrorCode Code;
}

public struct PlayStateResponse
{
    public bool IsPlaying;
}

public struct VolumeResponse
{
    public float Volume;
}

// Device paging

public struct GetDevicesRequest
{
    public byte Page;
}

public struct DeviceEntry
{
    public int Id;
    public string? Name;
}
