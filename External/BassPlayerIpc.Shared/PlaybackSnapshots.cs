using System.Buffers.Binary;

namespace BassPlayerIpc.Shared;

public readonly record struct ProgressSnapshot(long Revision, long Epoch, long CurrentMs, long TotalMs, long Timestamp, bool Playing, long SeekId = 0,
    double PlaybackRate = 1, long GaplessToken = 0, long TransitionToken = 0, long TransitionEpoch = 0, long TransitionCurrentMs = 0,
    long TransitionTotalMs = 0, double TransitionPlaybackRate = 1, long PlaybackId = 0);
/// <summary>The local occurrence that actually drained. Epoch distinguishes seeks and restarts on the same occurrence.</summary>
public readonly record struct PlaybackEnded(long PlaybackId, long Epoch)
{
    public const int Size = sizeof(long) * 2;

    public void Write(Span<byte> payload)
    {
        BinaryPrimitives.WriteInt64LittleEndian(payload, PlaybackId);
        BinaryPrimitives.WriteInt64LittleEndian(payload[sizeof(long)..], Epoch);
    }

    public static bool TryRead(ReadOnlySpan<byte> payload, out PlaybackEnded ended)
    {
        ended = default;
        if (payload.Length != Size) return false;
        ended = new(BinaryPrimitives.ReadInt64LittleEndian(payload), BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(long)..]));
        return ended.PlaybackId >= 0 && ended.Epoch > 0;
    }
}

public sealed record DspStateSnapshot(long Revision, DspState State);

public static class ProgressProtocol
{
    public const int Size = 72;
    public const int TransitionSize = Size + 40;
    public const int WithPlaybackIdSize = Size + sizeof(long);
    public const int TransitionWithPlaybackIdSize = TransitionSize + sizeof(long);

    public static int Write(Span<byte> bytes, ProgressSnapshot value)
    {
        WriteBase(bytes, value);
        return WritePlaybackId(bytes, Size, value.PlaybackId);
    }

    private static void WriteBase(Span<byte> bytes, ProgressSnapshot value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value.Revision);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], value.Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[16..], value.CurrentMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[24..], value.TotalMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[32..], value.Timestamp);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[40..], value.Playing ? 1 : 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[48..], value.SeekId);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[56..], value.PlaybackRate);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[64..], value.GaplessToken);
    }

    public static int WriteTransition(Span<byte> bytes, ProgressSnapshot value)
    {
        ValidateTransition(value);
        WriteBase(bytes, value);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[72..], value.TransitionToken);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[80..], value.TransitionEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[88..], value.TransitionCurrentMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[96..], value.TransitionTotalMs);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[104..], value.TransitionPlaybackRate);
        return WritePlaybackId(bytes, TransitionSize, value.PlaybackId);
    }

    private static int WritePlaybackId(Span<byte> bytes, int size, long playbackId)
    {
        if (playbackId == 0) return size;
        if (playbackId < 0) throw new ArgumentException("Invalid playback occurrence.");
        BinaryPrimitives.WriteInt64LittleEndian(bytes[size..], playbackId);
        return size + sizeof(long);
    }

    public static ProgressSnapshot Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (56 or Size or TransitionSize or WithPlaybackIdSize or TransitionWithPlaybackIdSize))
            throw new InvalidDataException("Invalid progress snapshot.");
        double rate = bytes.Length >= Size ? BinaryPrimitives.ReadDoubleLittleEndian(bytes[56..]) : 1;
        if (!double.IsFinite(rate) || rate is < 0.25 or > 5) throw new InvalidDataException("Invalid playback rate.");
        var progress = new ProgressSnapshot(BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]) != 0,
            BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]), rate, bytes.Length >= Size ? BinaryPrimitives.ReadInt64LittleEndian(bytes[64..]) : 0);
        if (bytes.Length is WithPlaybackIdSize or TransitionWithPlaybackIdSize)
        {
            long playbackId = BinaryPrimitives.ReadInt64LittleEndian(bytes[^sizeof(long)..]);
            if (playbackId <= 0) throw new InvalidDataException("Invalid playback occurrence.");
            progress = progress with { PlaybackId = playbackId };
        }
        if (bytes.Length is not (TransitionSize or TransitionWithPlaybackIdSize)) return progress;
        progress = progress with
        {
            TransitionToken = BinaryPrimitives.ReadInt64LittleEndian(bytes[72..]),
            TransitionEpoch = BinaryPrimitives.ReadInt64LittleEndian(bytes[80..]),
            TransitionCurrentMs = BinaryPrimitives.ReadInt64LittleEndian(bytes[88..]),
            TransitionTotalMs = BinaryPrimitives.ReadInt64LittleEndian(bytes[96..]),
            TransitionPlaybackRate = BinaryPrimitives.ReadDoubleLittleEndian(bytes[104..]),
        };
        ValidateTransition(progress);
        return progress;
    }

    private static void ValidateTransition(ProgressSnapshot value)
    {
        if (value.TransitionToken <= 0 || value.Epoch <= 0
            || value.TransitionEpoch <= 0 || value.TransitionEpoch == value.Epoch || value.TransitionCurrentMs < 0 || value.TransitionTotalMs < 0
            || value.TransitionTotalMs > 0 && value.TransitionCurrentMs > value.TransitionTotalMs
            || !double.IsFinite(value.TransitionPlaybackRate) || value.TransitionPlaybackRate is < 0.25 or > 5)
            throw new InvalidDataException("Invalid transition progress snapshot.");
    }
}
