namespace BassPlayerIpc.Shared;

public static class IpcConstants
{
    // Test processes opt into private kernel objects; production uses the original names.
    public static readonly string Scope = ReadScope();
    private static string ReadScope()
    {
        string? value = Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE");
        return !string.IsNullOrEmpty(value) && value.Length <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') ? "_" + value : "";
    }
    public static readonly string MmfName = "AudioPlayer_SharedMemory" + Scope;
    public static readonly string RequestSemaphoreName = "AudioPlayer_RequestReady" + Scope;
    public static readonly string ResponseSemaphoreName = "AudioPlayer_ResponseReady" + Scope;
    public static readonly string NotificationSemaphoreName = "AudioPlayer_NotificationReady" + Scope;
    public static readonly string MutexName = "AudioPlayer_SingleInstanceMutex" + Scope;
    public static readonly string ClientAliveMutexName = "WinUIMusicPlayer_SingleInstanceMutex" + Scope;

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
