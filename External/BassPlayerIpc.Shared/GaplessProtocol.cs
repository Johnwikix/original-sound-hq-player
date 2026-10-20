using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>Epoch guards stale queue plans; token identifies the exact UI queue entry/selection.</summary>
public sealed record GaplessRequest(long Epoch, long Token, string Path, bool PreserveContinuity = false)
{
    public byte[] Write()
    {
        byte[] path = Encoding.UTF8.GetBytes(Path);
        if (path.Length > (PreserveContinuity ? 2030 : 2032) || Path.Contains('\0')
            || Epoch < 0 || Token < 0 || Path.Length > 0 && (Epoch == 0 || Token == 0))
            throw new ArgumentException("Invalid next-track request.");
        int headerSize = PreserveContinuity ? 18 : 16;
        byte[] bytes = new byte[headerSize + path.Length];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, Epoch);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), Token);
        // A NUL cannot start a legacy path. It marks the optional flags header.
        if (PreserveContinuity) bytes[17] = 1;
        path.CopyTo(bytes, headerSize);
        return bytes;
    }

    public static GaplessRequest Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is < 16 or > 2048) throw new ArgumentException("Invalid next-track payload.");
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(bytes), token = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        bool extended = bytes.Length > 16 && bytes[16] == 0;
        if (extended && (bytes.Length < 18 || bytes[17] > 1)) throw new ArgumentException("Invalid next-track flags.");
        string path = new UTF8Encoding(false, true).GetString(bytes[(extended ? 18 : 16)..]);
        if (epoch < 0 || token < 0 || path.Contains('\0') || path.Length > 0 && (epoch == 0 || token == 0)) throw new ArgumentException("Invalid next-track request.");
        return new(epoch, token, path, extended && bytes[17] != 0);
    }
}
