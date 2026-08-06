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

    public const int EnvelopeHeaderSize = 5; // int16 + int16 + byte(seq)

    // Versioned mailbox layout: each region is guarded by a monotonically increasing
    // version int that is published AFTER the payload write completes, so a reader
    // observing a version change can safely read the full payload without tearing.
    public const long RequestVersionOffset = 0;
    public const long RequestBufferOffset = 4;
    public const long ResponseVersionOffset = RequestBufferOffset + MaxRequestSize;
    public const long ResponseBufferOffset = ResponseVersionOffset + 4;
    public const long NotificationVersionOffset = ResponseBufferOffset + MaxResponseSize;
    public const long NotificationSlot1Offset = NotificationVersionOffset + 4;
    public const long NotificationSlot2Offset = NotificationSlot1Offset + MaxNotificationSize;

    public static readonly long MmfSize = NotificationSlot2Offset + MaxNotificationSize;
}
