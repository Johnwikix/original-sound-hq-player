using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;

namespace BassPlayerIpc.Shared;

public static class BinarySerializer
{
    // ──────────────────────── Size constants ────────────────────────

    public const int MaxStringBytes = 512;
    public const int StringHeaderSize = 2; // ushort

    public const int PlayRequestSize = StringHeaderSize + MaxStringBytes;
    public const int SetMusicUrlRequestSize = StringHeaderSize + MaxStringBytes;
    public const int ChangePositionRequestSize = 8;
    public const int ChangeVolumeRequestSize = 8;
    public const int AdjustPlaybackPositionRequestSize = 4;
    public const int IpcSettingSize = StringHeaderSize + MaxStringBytes + 4 + 4 + 4 + 1 + 4 + 4 + 1 + 4 + 1 + 1;
    public const int SetEqualizerGainRequestSize = 1 + 4;
    public const int UpdateEqRequestSize = 40;
    public const int FailedResponseSize = 2;
    public const int PlayStateResponseSize = 1;
    public const int PositionResponseSize = 8;
    public const int VolumeResponseSize = 4;

    // ──────────────────────── String helpers ────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int WriteString(Span<byte> dest, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            BinaryPrimitives.WriteUInt16LittleEndian(dest, 0);
            return 2;
        }
        int maxData = Math.Min(dest.Length - 2, MaxStringBytes);
        int byteCount = Encoding.UTF8.GetBytes(value.AsSpan(), dest[2..]);
        if (byteCount > maxData) byteCount = maxData;
        BinaryPrimitives.WriteUInt16LittleEndian(dest, (ushort)byteCount);
        return 2 + byteCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static string ReadString(ReadOnlySpan<byte> src, out int bytesRead)
    {
        if (src.Length < 2) { bytesRead = 0; return string.Empty; }
        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(src);
        if (len == 0) { bytesRead = 2; return string.Empty; }
        int readLen = Math.Min(len, src.Length - 2);
        var result = Encoding.UTF8.GetString(src.Slice(2, readLen));
        bytesRead = 2 + len;
        return result;
    }

    // ──────────────────────── Request writers ────────────────────────

    public static int WritePlayRequest(Span<byte> dest, PlayRequest req) => WriteString(dest, req.Url);
    public static PlayRequest ReadPlayRequest(ReadOnlySpan<byte> src) => new() { Url = ReadString(src, out _) };

    public static int WriteSetMusicUrlRequest(Span<byte> dest, SetMusicUrlRequest req) => WriteString(dest, req.Url);
    public static SetMusicUrlRequest ReadSetMusicUrlRequest(ReadOnlySpan<byte> src) => new() { Url = ReadString(src, out _) };

    public static int WriteChangePositionRequest(Span<byte> dest, ChangePositionRequest req)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(dest, req.PositionSeconds);
        return 8;
    }

    public static ChangePositionRequest ReadChangePositionRequest(ReadOnlySpan<byte> src)
    {
        return new() { PositionSeconds = BinaryPrimitives.ReadDoubleLittleEndian(src) };
    }

    public static int WriteChangeVolumeRequest(Span<byte> dest, ChangeVolumeRequest req)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(dest, req.Volume);
        return 8;
    }

    public static ChangeVolumeRequest ReadChangeVolumeRequest(ReadOnlySpan<byte> src)
    {
        return new() { Volume = BinaryPrimitives.ReadDoubleLittleEndian(src) };
    }

    public static int WriteAdjustPlaybackPositionRequest(Span<byte> dest, AdjustPlaybackPositionRequest req)
    {
        BinaryPrimitives.WriteInt32LittleEndian(dest, req.Seconds);
        return 4;
    }

    public static AdjustPlaybackPositionRequest ReadAdjustPlaybackPositionRequest(ReadOnlySpan<byte> src)
    {
        return new() { Seconds = BinaryPrimitives.ReadInt32LittleEndian(src) };
    }

    // ──────────────────────── IpcSetting ────────────────────────

    public static int WriteIpcSetting(Span<byte> dest, IpcSetting s)
    {
        int offset = WriteString(dest, s.OutputMode);
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], s.BassOutputDeviceId); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], s.BassASIODeviceId); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], s.Latency); offset += 4;
        dest[offset++] = s.IsDopEnabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], s.DsdGain); offset += 4;
        BinaryPrimitives.WriteInt32LittleEndian(dest[offset..], s.DsdPcmFreq); offset += 4;
        dest[offset++] = s.IsEqualizerEnabled ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], s.Volume); offset += 4;
        dest[offset++] = s.IsSettingChanged ? (byte)1 : (byte)0;
        dest[offset++] = s.IsFadeEnabled ? (byte)1 : (byte)0;
        return offset;
    }

    public static IpcSetting ReadIpcSetting(ReadOnlySpan<byte> src)
    {
        var s = new IpcSetting();
        int offset;
        s.OutputMode = ReadString(src, out offset);
        s.BassOutputDeviceId = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]); offset += 4;
        s.BassASIODeviceId = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]); offset += 4;
        s.Latency = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]); offset += 4;
        s.IsDopEnabled = src[offset++] != 0;
        s.DsdGain = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]); offset += 4;
        s.DsdPcmFreq = BinaryPrimitives.ReadInt32LittleEndian(src[offset..]); offset += 4;
        s.IsEqualizerEnabled = src[offset++] != 0;
        s.Volume = BinaryPrimitives.ReadSingleLittleEndian(src[offset..]); offset += 4;
        s.IsSettingChanged = src[offset++] != 0;
        s.IsFadeEnabled = src[offset++] != 0;
        return s;
    }

    // ──────────────────────── SetEqualizerGainRequest ────────────────────────

    public static int WriteSetEqualizerGainRequest(Span<byte> dest, SetEqualizerGainRequest req)
    {
        dest[0] = req.BandIndex;
        BinaryPrimitives.WriteSingleLittleEndian(dest[1..], req.Gain);
        return 5;
    }

    public static SetEqualizerGainRequest ReadSetEqualizerGainRequest(ReadOnlySpan<byte> src)
    {
        return new()
        {
            BandIndex = src[0],
            Gain = BinaryPrimitives.ReadSingleLittleEndian(src[1..])
        };
    }

    // ──────────────────────── UpdateEqRequest ────────────────────────

    public static int WriteUpdateEqRequest(Span<byte> dest, UpdateEqRequest req)
    {
        int offset = 0;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band0); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band1); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band2); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band3); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band4); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band5); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band6); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band7); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band8); offset += 4;
        BinaryPrimitives.WriteSingleLittleEndian(dest[offset..], req.Band9); offset += 4;
        return offset;
    }

    public static UpdateEqRequest ReadUpdateEqRequest(ReadOnlySpan<byte> src)
    {
        return new()
        {
            Band0 = BinaryPrimitives.ReadSingleLittleEndian(src),
            Band1 = BinaryPrimitives.ReadSingleLittleEndian(src[4..]),
            Band2 = BinaryPrimitives.ReadSingleLittleEndian(src[8..]),
            Band3 = BinaryPrimitives.ReadSingleLittleEndian(src[12..]),
            Band4 = BinaryPrimitives.ReadSingleLittleEndian(src[16..]),
            Band5 = BinaryPrimitives.ReadSingleLittleEndian(src[20..]),
            Band6 = BinaryPrimitives.ReadSingleLittleEndian(src[24..]),
            Band7 = BinaryPrimitives.ReadSingleLittleEndian(src[28..]),
            Band8 = BinaryPrimitives.ReadSingleLittleEndian(src[32..]),
            Band9 = BinaryPrimitives.ReadSingleLittleEndian(src[36..]),
        };
    }

    // ──────────────────────── Response writers ────────────────────────

    public static int WriteFailedResponse(Span<byte> dest, FailedResponse resp)
    {
        BinaryPrimitives.WriteInt16LittleEndian(dest, (short)resp.Code);
        return 2;
    }

    public static FailedResponse ReadFailedResponse(ReadOnlySpan<byte> src)
    {
        return new() { Code = (ErrorCode)BinaryPrimitives.ReadInt16LittleEndian(src) };
    }

    public static int WritePlayStateResponse(Span<byte> dest, PlayStateResponse resp)
    {
        dest[0] = resp.IsPlaying ? (byte)1 : (byte)0;
        return 1;
    }

    public static PlayStateResponse ReadPlayStateResponse(ReadOnlySpan<byte> src)
    {
        return new() { IsPlaying = src[0] != 0 };
    }

    public static int WritePositionResponse(Span<byte> dest, PositionResponse resp)
    {
        BinaryPrimitives.WriteDoubleLittleEndian(dest, resp.PositionSeconds);
        return 8;
    }

    public static PositionResponse ReadPositionResponse(ReadOnlySpan<byte> src)
    {
        return new() { PositionSeconds = BinaryPrimitives.ReadDoubleLittleEndian(src) };
    }

    public static int WriteVolumeResponse(Span<byte> dest, VolumeResponse resp)
    {
        BinaryPrimitives.WriteSingleLittleEndian(dest, resp.Volume);
        return 4;
    }

    public static VolumeResponse ReadVolumeResponse(ReadOnlySpan<byte> src)
    {
        return new() { Volume = BinaryPrimitives.ReadSingleLittleEndian(src) };
    }
}
