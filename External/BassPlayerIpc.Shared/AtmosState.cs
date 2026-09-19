namespace BassPlayerIpc.Shared;

public enum AtmosPlaybackStatus : byte
{
    Off, Waiting, Ready, Active, PcmFallback, Stopped
}

public enum AtmosFailure : byte
{
    None, UnsupportedFormat, DeviceUnavailable, DeviceBusy, ExclusiveDenied,
    OutputFailed, DecodeFailed, SelectDevice
}

public enum ActualOutputMode : byte { None, Shared, Exclusive, Asio }
