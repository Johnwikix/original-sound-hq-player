namespace BassPlayerIpc.Shared;

public static class IpcConstants
{
    public const string MmfName = "BassPlayerSharp_SharedMemory";
    public const string RequestSemaphoreName = "BassPlayerSharp_RequestReady";
    public const string ResponseSemaphoreName = "BassPlayerSharp_ResponseReady";
    public const string NotificationSemaphoreName = "BassPlayerSharp_NotificationReady";
    public const string MutexName = "BassPlayerSharp_SingleInstanceMutex";
    public const string ClientAliveMutexName = "WinUIMusicPlayer_SingleInstanceMutex";

    public const int MaxRequestSize = 2048;
    public const int MaxResponseSize = 512;
    public const int MaxNotificationSize = 512;

    public const int EnvelopeHeaderSize = 4; // int16 + int16

    public static readonly long MmfSize = MaxRequestSize + MaxResponseSize + MaxNotificationSize;
    public const long RequestBufferOffset = 0;
    public static readonly long ResponseBufferOffset = MaxRequestSize;
    public static readonly long NotificationBufferOffset = MaxRequestSize + MaxResponseSize;

    public const int NotificationSlotOffset = 1024; // offset within notification buffer for slot index (int32)
}
