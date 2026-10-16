using System.Buffers.Binary;

namespace AudioPlayer.AudioEnhancement;

// 仅回归工程使用：将内部状态转换为既有基准文件的存储格式。
public sealed partial class AllPassProcessor
{
    internal byte[] SnapshotState()
    {
        byte[] bytes=new byte[0x64CC];
        for(int i=0;i<longDelay.Length;i++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i*4,4),longDelay[i]);
        for(int i=0;i<shortDelay.Length;i++)BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x6350+i*4,4),shortDelay[i]);
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(0x64A0,4),gain);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x64A4,4),longPosition);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x64A8,4),shortPosition);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x64AC,4),SampleRate);
        for(int i=0;i<7;i++)BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x64B0+i*4,4),lengths[i]);
        return bytes;
    }
}
