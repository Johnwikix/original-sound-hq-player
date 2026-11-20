using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>Epoch guards stale queue plans; token identifies the exact UI queue entry/selection.</summary>
/// <param name="Epoch">The current playback epoch.</param>
/// <param name="Token">The positive identity of a queued track, or zero for cancellation.</param>
/// <param name="Path">The next track path, or empty for cancellation.</param>
/// <param name="PreserveAlbumBoundary">Keep the original track boundary in Smart mode.</param>
public sealed record GaplessRequest(long Epoch, long Token, string Path, bool PreserveAlbumBoundary = false)
{
    private const int HeaderSize = 16;
    private const int MaxPathBytes = 2032;
    // 复用旧协议禁止的符号位，保留完整令牌范围和路径容量。
    private const long AlbumBoundaryFlag = long.MinValue;
    private static readonly UTF8Encoding PathEncoding = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public byte[] Write()
    {
        ArgumentNullException.ThrowIfNull(Path);
        Validate(Epoch, Token, Path.AsSpan(), PreserveAlbumBoundary);
        int pathLength = PathEncoding.GetByteCount(Path.AsSpan());
        if (pathLength > MaxPathBytes) throw new ArgumentException("Invalid next-track request.");
        byte[] bytes = new byte[HeaderSize + pathLength];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), PreserveAlbumBoundary ? Token | AlbumBoundaryFlag : Token);
        PathEncoding.GetBytes(Path.AsSpan(), bytes.AsSpan(HeaderSize));
        return bytes;
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

    private static void Validate(long epoch, long token, ReadOnlySpan<char> path, bool preserveAlbumBoundary)
    {
        if (epoch < 0 || token < 0 || path.Contains('\0') || (path.IsEmpty
            ? preserveAlbumBoundary
            : epoch == 0 || token == 0))
            throw new ArgumentException("Invalid next-track request.");
    }
}
