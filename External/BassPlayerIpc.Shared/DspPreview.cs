using System.Buffers.Binary;
using System.Text;

namespace BassPlayerIpc.Shared;

/// <summary>试听的发起顺序和实际输出代数，防止延迟命令污染下一输出。</summary>
public readonly record struct DspPreview(long Sequence, long Generation, string DeviceId, bool End, DspSettings Settings)
{
    public const int Size = DspProtocol.SettingsSize + 275;

    public void Write(Span<byte> data)
    {
        data[..Size].Clear();
        DspProtocol.WriteSettings(data, Settings);
        var target = data[DspProtocol.SettingsSize..];
        BinaryPrimitives.WriteInt64LittleEndian(target, Sequence);
        BinaryPrimitives.WriteInt64LittleEndian(target[8..], Generation);
        target[16] = End ? (byte)1 : (byte)0;
        int length = Encoding.UTF8.GetBytes(DeviceId, target.Slice(19, 256));
        BinaryPrimitives.WriteUInt16LittleEndian(target[17..], (ushort)length);
    }

    public static DspPreview Read(ReadOnlySpan<byte> data)
    {
        if (data.Length != Size) throw new ArgumentException("Invalid preview payload.");
        var target = data[DspProtocol.SettingsSize..];
        int length = BinaryPrimitives.ReadUInt16LittleEndian(target[17..]);
        if (length > 256 || target[16] > 1) throw new ArgumentException("Invalid preview target.");
        return new(BinaryPrimitives.ReadInt64LittleEndian(target), BinaryPrimitives.ReadInt64LittleEndian(target[8..]),
            new UTF8Encoding(false, true).GetString(target.Slice(19, length)), target[16] != 0,
            DspProtocol.ReadSettings(data[..DspProtocol.SettingsSize]));
    }
}
