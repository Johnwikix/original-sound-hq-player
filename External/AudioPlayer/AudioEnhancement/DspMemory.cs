using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

/// <summary>保存 DSP 状态，并提供固定系数数据的访问接口。</summary>
/// <remarks>状态、输入输出和工作缓冲区独立持有存储，系数使用只读资源句柄。</remarks>
internal sealed class DspMemory
{
    // 高位选择缓冲区，低位为缓冲区内偏移；资源使用独立的标志位。
    private const int BufferShift = 16;
    private const uint OffsetMask = (1u << BufferShift) - 1;
    internal const uint StateAddress = 1u << BufferShift;
    internal const uint InputAddress = 2u << BufferShift;
    internal const uint OutputAddress = 3u << BufferShift;
    private readonly byte[][] _buffers = new byte[4 + 13][];
    private int _bufferCount = 4;

    internal DspMemory()
    {
        _buffers[1] = new byte[DspStateLayout.ByteLength];
        _buffers[2] = new byte[4096];
        _buffers[3] = new byte[4096];
    }

    internal uint AllocateScratch(int length)
    {
        if ((uint)length > OffsetMask || _bufferCount == _buffers.Length)
            throw new ArgumentOutOfRangeException(nameof(length));
        int index = _bufferCount++;
        _buffers[index] = new byte[length];
        return (uint)index << BufferShift;
    }

    private uint _cachedPrototype;
    private int _cachedPrototypeLength;
    private float[]? _prototypePairs;

    /// <summary>获取经过边界检查的类型化视图，避免逐样本地址路由和位转换。</summary>
    internal ReadOnlySpan<float> Floats(uint address, int count) => MemoryMarshal.Cast<byte,float>(Read(address,checked(count*4)));
    /// <summary>获取信号缓冲区经过边界检查的可写类型化视图。</summary>
    internal Span<float> WritableFloats(uint address, int count) => MemoryMarshal.Cast<byte,float>(Write(address,checked(count*4)));

    /// <summary>按抽头优先顺序缓存固定合成原型中经过 float 舍入的对称样本对。</summary>
    internal ReadOnlySpan<float> PrototypePairs(uint address, int length)
    {
        if(_prototypePairs is not null && _cachedPrototype==address && _cachedPrototypeLength==length)return _prototypePairs;
        ReadOnlySpan<float> prototype=Floats(address,checked(length+48));
        float[] pairs=new float[checked(length*25)];
        for(int i=0;i<length;i++)
        {
            pairs[i]=prototype[24+i];
            for(int radius=1;radius<=24;radius++)
                pairs[radius*length+i]=(float)(prototype[24+i-radius]+(double)prototype[24+i+radius]);
        }
        _cachedPrototype=address;_cachedPrototypeLength=length;_prototypePairs=pairs;
        return pairs;
    }

    internal byte U8(uint address) => Read(address, 1)[0];
    internal sbyte I8(uint address) => unchecked((sbyte)U8(address));
    internal short I16(uint address) => BinaryPrimitives.ReadInt16LittleEndian(Read(address, 2));
    internal ushort U16(uint address) => BinaryPrimitives.ReadUInt16LittleEndian(Read(address, 2));
    internal int I32(uint address) => BinaryPrimitives.ReadInt32LittleEndian(Read(address, 4));
    internal uint U32(uint address) => unchecked((uint)I32(address));
    internal float F32(uint address) => BitConverter.Int32BitsToSingle(I32(address));
    internal double F64(uint address) => BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(Read(address, 8)));

    internal void U8(uint address, byte value) => Write(address, 1)[0] = value;
    internal void I16(uint address, short value) => BinaryPrimitives.WriteInt16LittleEndian(Write(address, 2), value);
    internal void U16(uint address, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(Write(address, 2), value);
    internal void I32(uint address, int value) => BinaryPrimitives.WriteInt32LittleEndian(Write(address, 4), value);
    internal void U32(uint address, uint value) => I32(address, unchecked((int)value));
    internal void F32(uint address, float value) => I32(address, BitConverter.SingleToInt32Bits(value));
    internal void F32(uint address, double value) => F32(address, (float)value);
    internal void F64(uint address, double value) => BinaryPrimitives.WriteInt64LittleEndian(Write(address, 8), BitConverter.DoubleToInt64Bits(value));

    internal ReadOnlySpan<byte> Read(uint address, int length) => CoefficientStore.IsCoefficient(address)
        ? CoefficientStore.Default.Read(address, length)
        : _buffers[address >> BufferShift].AsSpan((int)(address & OffsetMask), length);
    internal Span<byte> Write(uint address, int length)
    {
        if (CoefficientStore.IsCoefficient(address)) throw new ArgumentOutOfRangeException(nameof(address));
        return _buffers[address >> BufferShift].AsSpan((int)(address & OffsetMask), length);
    }
    internal void Clear(uint address, int length) => Write(address, length).Clear();
    internal void Copy(uint destination, uint source, int length) => Read(source, length).CopyTo(Write(destination, length));
    internal byte[] Snapshot(uint address, int length) => Read(address, length).ToArray();
}
