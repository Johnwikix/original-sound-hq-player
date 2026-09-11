namespace BassPlayerIpc.Shared;

public static class IpcConstants
{
    public const string MmfName = "AudioPlayer_SharedMemory";
    public const string RequestSemaphoreName = "AudioPlayer_RequestReady";
    public const string ResponseSemaphoreName = "AudioPlayer_ResponseReady";
    public const string NotificationSemaphoreName = "AudioPlayer_NotificationReady";
    public const string MutexName = "AudioPlayer_SingleInstanceMutex";
    public const string ClientAliveMutexName = "WinUIMusicPlayer_SingleInstanceMutex";

    public const int MaxRequestSize = 2048;
    public const int MaxResponseSize = 512;
    public const int MaxNotificationSize = 512;

    public const int EnvelopeHeaderSize = 5; // int16 + int16 + byte(seq)

    // 单请求在途：响应版本回显完整请求版本，确认前禁止复用请求槽。
    // 版本在载荷之后发布；通知槽为独立的最新状态双缓冲。
    public const long RequestVersionOffset = 0;
    public const long RequestBufferOffset = 4;
    public const long ResponseVersionOffset = RequestBufferOffset + MaxRequestSize;
    public const long ResponseBufferOffset = ResponseVersionOffset + 4;
    public const long NotificationVersionOffset = ResponseBufferOffset + MaxResponseSize;
    public const long NotificationSlot1Offset = NotificationVersionOffset + 4;
    public const long NotificationSlot2Offset = NotificationSlot1Offset + MaxNotificationSize;

    public static readonly long MmfSize = NotificationSlot2Offset + MaxNotificationSize;
}
