using System.Buffers.Binary;

namespace BassPlayerIpc.Shared;

[Flags]
public enum FftFrameFlags : int
{
    None = 0,
    Available = 1,
    Discontinuity = 2,
}

/// <summary>
/// Latest FFT snapshot exchanged between AudioPlayer and the GUI. The arrays are
/// exactly the positive-frequency bins (including DC and Nyquist) for left and right.
/// </summary>
public sealed record FftSnapshot(
    long Sequence,
    long Epoch,
    int SampleRate,
    int FftSize,
    int HopSize,
    FftFrameFlags Flags,
    float[] Left,
    float[] Right)
{
    public bool IsAvailable => (Flags & FftFrameFlags.Available) != 0;
    public int BinCount => IsAvailable ? FftSize / 2 + 1 : 0;
}

public static class FftProtocol
{
    // sequence, epoch, sample rate, FFT size, hop, flags, bins, reserved
    public const int HeaderSize = 40;
    public const int MaxFftSize = 4096;
    public const int MaxBinCount = MaxFftSize / 2 + 1;
    public const int MaxPayloadSize = HeaderSize + MaxBinCount * sizeof(float) * 2;

    public static int Write(Span<byte> destination, FftSnapshot snapshot)
    {
        if (snapshot.Sequence <= 0) throw new ArgumentOutOfRangeException(nameof(snapshot));
        if (snapshot.Epoch < 0 || snapshot.SampleRate < 0 || snapshot.FftSize < 0 || snapshot.HopSize < 0)
            throw new ArgumentOutOfRangeException(nameof(snapshot));
        if ((snapshot.Flags & ~ (FftFrameFlags.Available | FftFrameFlags.Discontinuity)) != 0)
            throw new ArgumentException("Invalid FFT flags.", nameof(snapshot));

        int bins = snapshot.IsAvailable ? snapshot.FftSize / 2 + 1 : 0;
        if (snapshot.IsAvailable && (snapshot.FftSize < 2 || snapshot.FftSize > MaxFftSize
            || (snapshot.FftSize & (snapshot.FftSize - 1)) != 0
            || snapshot.HopSize <= 0 || snapshot.HopSize > snapshot.FftSize
            || snapshot.Left.Length < bins || snapshot.Right.Length < bins))
            throw new ArgumentException("Invalid FFT snapshot dimensions.", nameof(snapshot));
        if (!snapshot.IsAvailable && (snapshot.FftSize != 0 || snapshot.HopSize != 0
            || snapshot.Left.Length != 0 || snapshot.Right.Length != 0))
            throw new ArgumentException("Unavailable FFT snapshots must not contain bins.", nameof(snapshot));
        int length = checked(HeaderSize + bins * sizeof(float) * 2);
        if (destination.Length < length) throw new ArgumentException("Destination is too small.", nameof(destination));

        BinaryPrimitives.WriteInt64LittleEndian(destination, snapshot.Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(destination[8..], snapshot.Epoch);
        BinaryPrimitives.WriteInt32LittleEndian(destination[16..], snapshot.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], snapshot.FftSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination[24..], snapshot.HopSize);
        BinaryPrimitives.WriteInt32LittleEndian(destination[28..], (int)snapshot.Flags);
        BinaryPrimitives.WriteInt32LittleEndian(destination[32..], bins);
        BinaryPrimitives.WriteInt32LittleEndian(destination[36..], 0);

        var target = destination[HeaderSize..];
        for (int i = 0; i < bins; i++)
        {
            float value = snapshot.Left[i];
            if (!float.IsFinite(value) || value < 0) throw new ArgumentException("Invalid left FFT magnitude.", nameof(snapshot));
            BinaryPrimitives.WriteInt32LittleEndian(target[(i * 4)..], BitConverter.SingleToInt32Bits(value));
        }
        target = target[(bins * 4)..];
        for (int i = 0; i < bins; i++)
        {
            float value = snapshot.Right[i];
            if (!float.IsFinite(value) || value < 0) throw new ArgumentException("Invalid right FFT magnitude.", nameof(snapshot));
            BinaryPrimitives.WriteInt32LittleEndian(target[(i * 4)..], BitConverter.SingleToInt32Bits(value));
        }
        return length;
    }

    public static FftSnapshot Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < HeaderSize) throw new InvalidDataException("Invalid FFT snapshot.");
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(source);
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(source[8..]);
        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(source[16..]);
        int fftSize = BinaryPrimitives.ReadInt32LittleEndian(source[20..]);
        int hopSize = BinaryPrimitives.ReadInt32LittleEndian(source[24..]);
        var flags = (FftFrameFlags)BinaryPrimitives.ReadInt32LittleEndian(source[28..]);
        int bins = BinaryPrimitives.ReadInt32LittleEndian(source[32..]);
        if (sequence <= 0 || epoch < 0 || sampleRate < 0 || (flags & ~(FftFrameFlags.Available | FftFrameFlags.Discontinuity)) != 0)
            throw new InvalidDataException("Invalid FFT snapshot header.");
        if ((flags & FftFrameFlags.Available) != 0)
        {
            if (fftSize < 2 || fftSize > MaxFftSize || (fftSize & (fftSize - 1)) != 0
                || hopSize <= 0 || hopSize > fftSize || bins != fftSize / 2 + 1 || bins > MaxBinCount)
                throw new InvalidDataException("Invalid FFT snapshot dimensions.");
        }
        else if (bins != 0 || fftSize != 0 || hopSize != 0)
            throw new InvalidDataException("Unavailable FFT snapshot contains dimensions.");
        int expected = checked(HeaderSize + bins * sizeof(float) * 2);
        if (source.Length != expected) throw new InvalidDataException("Invalid FFT snapshot length.");

        var left = new float[bins];
        var right = new float[bins];
        var values = source[HeaderSize..];
        for (int i = 0; i < bins; i++)
        {
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(values[(i * 4)..]));
            if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid left FFT magnitude.");
            left[i] = value;
        }
        values = values[(bins * 4)..];
        for (int i = 0; i < bins; i++)
        {
            float value = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(values[(i * 4)..]));
            if (!float.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid right FFT magnitude.");
            right[i] = value;
        }
        return new(sequence, epoch, sampleRate, fftSize, hopSize, flags, left, right);
    }
}
