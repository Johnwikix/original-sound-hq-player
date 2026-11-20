using System.Buffers.Binary;

namespace BassPlayerIpc.Shared;

public readonly record struct ProgressSnapshot(long Revision, long Epoch, long CurrentMs, long TotalMs, long Timestamp, bool Playing, long SeekId = 0,
    double PlaybackRate = 1, long GaplessToken = 0, long TransitionToken = 0, long TransitionEpoch = 0, long TransitionCurrentMs = 0,
    long TransitionTotalMs = 0, double TransitionPlaybackRate = 1);
public sealed record DspStateSnapshot(long Revision, DspState State);

public static class ProgressProtocol
{
    public const int Size = 72;
    public const int TransitionSize = Size + 40;
    public static void Write(Span<byte> bytes, ProgressSnapshot value)
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

    public static void WriteTransition(Span<byte> bytes, ProgressSnapshot value)
    {
        ValidateTransition(value);
        Write(bytes, value);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[72..], value.TransitionToken);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[80..], value.TransitionEpoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[88..], value.TransitionCurrentMs);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[96..], value.TransitionTotalMs);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes[104..], value.TransitionPlaybackRate);
    }

    public static ProgressSnapshot Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is not (56 or Size or TransitionSize)) throw new InvalidDataException("Invalid progress snapshot.");
        double rate = bytes.Length >= Size ? BinaryPrimitives.ReadDoubleLittleEndian(bytes[56..]) : 1;
        if (!double.IsFinite(rate) || rate is < 0.25 or > 5) throw new InvalidDataException("Invalid playback rate.");
        var progress = new ProgressSnapshot(BinaryPrimitives.ReadInt64LittleEndian(bytes), BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[16..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[24..]),
            BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[40..]) != 0,
            BinaryPrimitives.ReadInt64LittleEndian(bytes[48..]), rate, bytes.Length >= Size ? BinaryPrimitives.ReadInt64LittleEndian(bytes[64..]) : 0);
        if (bytes.Length != TransitionSize) return progress;
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
