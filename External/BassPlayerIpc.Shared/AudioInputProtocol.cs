using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>Payload used by the range-aware commands. An absent range is invalid.</summary>
public static class AudioInputProtocol
{
    private const int RangeSize = sizeof(long) * 2 + sizeof(int);
    private static readonly UTF8Encoding PathEncoding = new(false, true);

    public static byte[] Write(AudioInput input) => Write(input, 0);

    internal static byte[] Write(AudioInput input, int prefixSize)
    {
        input.Validate();
        if (input.SampleRange is not { } range) throw new ArgumentException("A sample range is required.");
        int pathBytes = PathEncoding.GetByteCount(input.Path);
        if (prefixSize < 0 || prefixSize > IpcConstants.MaxRequestSize - RangeSize - pathBytes)
            throw new ArgumentException("The audio path exceeds payload capacity.");
        byte[] bytes = new byte[prefixSize + RangeSize + pathBytes];
        var payload = bytes.AsSpan(prefixSize);
        BinaryPrimitives.WriteInt64LittleEndian(payload, range.StartSample);
        BinaryPrimitives.WriteInt64LittleEndian(payload[8..], range.EndSample);
        BinaryPrimitives.WriteInt32LittleEndian(payload[16..], range.SampleRate);
        PathEncoding.GetBytes(input.Path.AsSpan(), payload[RangeSize..]);
        return bytes;
    }

    public static AudioInput Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= RangeSize or > IpcConstants.MaxRequestSize)
            throw new ArgumentException("Invalid audio input payload.");
        var range = new AudioSampleRange(BinaryPrimitives.ReadInt64LittleEndian(payload),
            BinaryPrimitives.ReadInt64LittleEndian(payload[8..]), BinaryPrimitives.ReadInt32LittleEndian(payload[16..]));
        var input = new AudioInput(PathEncoding.GetString(payload[RangeSize..]), range);
        input.Validate();
        return input;
    }

    public static byte[] WriteOccurrence(long playbackId, AudioInput input)
    {
        if (playbackId <= 0) throw new ArgumentOutOfRangeException(nameof(playbackId));
        byte[] bytes = Write(input, sizeof(long));
        BinaryPrimitives.WriteInt64LittleEndian(bytes, playbackId);
        return bytes;
    }

    public static (long PlaybackId, AudioInput Input) ReadOccurrence(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is <= sizeof(long) or > IpcConstants.MaxRequestSize)
            throw new ArgumentException("Invalid playback occurrence.");
        long id = BinaryPrimitives.ReadInt64LittleEndian(payload);
        if (id <= 0) throw new ArgumentException("Invalid playback occurrence.");
        return (id, Read(payload[sizeof(long)..]));
    }
}
