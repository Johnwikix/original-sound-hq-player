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
    AdjustPlaybackPosition = 11,

    // Equalizer
    ToggleEqualizer = 12,
    SetEqualizer = 13,
    ClearEqualizer = 14,
    SetEqualizerGain = 15,
    UpdateEq = 16,

    // Devices
    GetWasapiDevices = 17,
    GetAsioDevices = 18,
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
    PositionAdjusted = 7,
    VolumeWriteBack = 8,
    Exit = 9,
    NotificationDropped = 10,
    WasapiDevices = 11,
    AsioDevices = 12,
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
