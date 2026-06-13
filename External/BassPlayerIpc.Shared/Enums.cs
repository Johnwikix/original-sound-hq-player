namespace BassPlayerIpc.Shared;

public enum CommandId : short
{
    None = 0,

    // Playback
    Play = 1,
    PlayButton = 2,
    SetMusicUrl = 3,
    GetProgress = 4,
    GetDuration = 5,
    ChangePosition = 6,
    ChangeVolume = 7,
    MusicEnd = 8,
    FadeOut = 9,

    // Settings
    UpdateSettings = 10,

    // Equalizer
    ToggleEqualizer = 11,
    SetEqualizer = 12,
    ClearEqualizer = 13,
    SetEqualizerGain = 14,
    UpdateEq = 15,

    // Devices
    GetWasapiDevices = 16,
    GetAsioDevices = 17,

    // Time
    GetTimeProgress = 18,
}

public enum MessageTypeId : short
{
    None = 0,
    Failed = 1,
    Success = 2,
    PlayState = 3,
    PlayEnded = 4,
    CurrentTime = 5,
    TotalTime = 6,
    VolumeWriteBack = 7,
    Exit = 8,
    NotificationDropped = 9,
    WasapiDevices = 10,
    AsioDevices = 11,
    TimeProgress = 12,
}

public enum ErrorCode : short
{
    None = 0,
    Unknown = 1,
    FileNotFound = 2,
    InvalidCommand = 3,
    InvalidPayload = 4,
    PlaybackFailed = 5,
    BufferTooLarge = 6,
    DeviceNotAvailable = 7,
}
