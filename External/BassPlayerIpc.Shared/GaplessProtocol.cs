using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>Epoch guards stale queue plans; token identifies the exact UI queue entry/selection.</summary>
/// <param name="Epoch">The current playback epoch.</param>
/// <param name="Token">The positive identity of a queued track, or zero for cancellation.</param>
/// <param name="Path">The next track path, or empty for cancellation.</param>
/// <param name="PreserveAlbumBoundary">Keep the original track boundary in Smart mode.</param>
/// <param name="SampleRange">An optional interval in the original audio stream.</param>
public sealed record GaplessRequest(long Epoch, long Token, string Path, bool PreserveAlbumBoundary = false, AudioSampleRange? SampleRange = null)
{
    private const int HeaderSize = 16;
    private const int MaxPathBytes = 2032;
    // 复用旧协议禁止的符号位，保留完整令牌范围和路径容量。
    private const long AlbumBoundaryFlag = long.MinValue;
    private static readonly UTF8Encoding PathEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public AudioInput Input => new(Path, SampleRange);

    public byte[] Write()
    {
        ArgumentNullException.ThrowIfNull(Path);
        if (SampleRange.HasValue) throw new ArgumentException("Use the range-aware command for this request.");
        Validate(Epoch, Token, Path.AsSpan(), PreserveAlbumBoundary);
        int pathLength = PathEncoding.GetByteCount(Path.AsSpan());
        if (pathLength > MaxPathBytes) throw new ArgumentException("Invalid next-track request.");
        byte[] bytes = new byte[HeaderSize + pathLength];
        WriteIdentity(bytes);
        PathEncoding.GetBytes(Path.AsSpan(), bytes.AsSpan(HeaderSize));
        return bytes;
    }

    public byte[] WriteRanged()
    {
        Validate(Epoch, Token, Path.AsSpan(), PreserveAlbumBoundary);
        byte[] bytes = AudioInputProtocol.Write(Input, HeaderSize);
        WriteIdentity(bytes);
        return bytes;
    }

    private void WriteIdentity(Span<byte> bytes)
    {
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], PreserveAlbumBoundary ? Token | AlbumBoundaryFlag : Token);
    }

    public static GaplessRequest Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < HeaderSize or > HeaderSize + MaxPathBytes) throw new ArgumentException("Invalid next-track payload.");
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        long encodedToken = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        bool preserveAlbumBoundary = (encodedToken & AlbumBoundaryFlag) != 0;
        long token = encodedToken & long.MaxValue;
        string path = PathEncoding.GetString(bytes[HeaderSize..]);
        Validate(epoch, token, path.AsSpan(), preserveAlbumBoundary);
        return new(epoch, token, path, preserveAlbumBoundary);
    }

    public static GaplessRequest ReadRanged(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= HeaderSize or > IpcConstants.MaxRequestSize)
            throw new ArgumentException("Invalid next-track payload.");
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        long encodedToken = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        long token = encodedToken & long.MaxValue;
        bool preserveAlbumBoundary = (encodedToken & AlbumBoundaryFlag) != 0;
        var input = AudioInputProtocol.Read(bytes[HeaderSize..]);
        Validate(epoch, token, input.Path.AsSpan(), preserveAlbumBoundary);
        return new(epoch, token, input.Path, preserveAlbumBoundary, input.SampleRange);
    }

    private static void Validate(long epoch, long token, ReadOnlySpan<char> path, bool preserveAlbumBoundary)
    {
        if (epoch < 0 || token < 0 || path.Contains('\0') || (path.IsEmpty
            ? preserveAlbumBoundary
            : epoch == 0 || token == 0))
            throw new ArgumentException("Invalid next-track request.");
    }
}

/// <summary>Queries an already admitted manual fade or a committed handoff without repeating its path.</summary>
public static class TransitionNowQuery
{
    public const int Size = sizeof(long) * 2;

    public static void Write(Span<byte> payload, long epoch, long token)
    {
        Validate(payload.Length, epoch, token);
        BinaryPrimitives.WriteInt64LittleEndian(payload, epoch);
        BinaryPrimitives.WriteInt64LittleEndian(payload[sizeof(long)..], token);
    }

    public static (long Epoch, long Token) Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != Size) throw new ArgumentException("Invalid transition query size.");
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(payload);
        long token = BinaryPrimitives.ReadInt64LittleEndian(payload[sizeof(long)..]);
        Validate(payload.Length, epoch, token);
        return (epoch, token);
    }

    private static void Validate(int size, long epoch, long token)
    {
        if (size != Size || epoch <= 0 || token <= 0) throw new ArgumentException("Invalid transition query.");
    }
}
