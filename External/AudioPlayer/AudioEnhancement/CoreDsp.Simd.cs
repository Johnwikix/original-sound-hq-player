using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    /// <summary>使用基础 x64 指令计算两个相位，兼容基础指令集的 Native AOT 构建。</summary>
    private static bool TryInterpolateSse2(DspMemory memory, uint descriptor, int ratio, uint input, uint output, int add)
    {
        if (!Sse2.IsSupported || ratio is not (8 or 16)) return false;
        const int count = 32;
        int radius = memory.I32(OffsetAddress(descriptor, 4));
        if (radius <= 0) return false;
        ReadOnlySpan<byte> inputBytes = memory.Read(input, checked((radius * 2 + count) * 4));
        ReadOnlySpan<byte> coefficientBytes = memory.Read(memory.U32(descriptor), checked((radius * ratio + 1) * 4));
        Span<byte> outputBytes = memory.Write(output, count * ratio * 4);
        if (inputBytes.Overlaps(outputBytes) || coefficientBytes.Overlaps(outputBytes)) return false;
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(inputBytes);
        ReadOnlySpan<float> filters = MemoryMarshal.Cast<byte, float>(coefficientBytes);
        Span<float> destination = MemoryMarshal.Cast<byte, float>(outputBytes);
        ref float filterBase = ref MemoryMarshal.GetReference(filters);
        ref float destinationBase = ref MemoryMarshal.GetReference(destination);
        Vector128<double> scale = Vector128.Create((double)ratio);
        Vector128<double> addScale = Vector128.Create((double)(uint)add);
        for (int n = radius; n < radius + count; n++)
        {
            for (int phase = 0; phase < ratio; phase += 2)
            {
                Vector128<float> sum = Vector128<float>.Zero;
                for (int j = 0; j < radius; j++)
                {
                    Vector128<double> left = Vector128.Create((double)samples[n - j]);
                    Vector128<double> right = Vector128.Create((double)samples[n + j + 1]);
                    Vector128<float> leftCoefficients = Vector64.LoadUnsafe(ref filterBase, (nuint)(j * ratio + phase)).ToVector128();
                    Vector128<float> rightCoefficients = Vector64.LoadUnsafe(ref filterBase, (nuint)((j + 1) * ratio - phase - 1)).ToVector128();
                    rightCoefficients = Sse.Shuffle(rightCoefficients, rightCoefficients, 0xb1);
                    sum = Sse2.ConvertToVector128Single(Sse2.Add(
                        Sse2.Multiply(left, Sse2.ConvertToVector128Double(leftCoefficients)), Sse2.ConvertToVector128Double(sum)));
                    sum = Sse2.ConvertToVector128Single(Sse2.Add(
                        Sse2.Multiply(right, Sse2.ConvertToVector128Double(rightCoefficients)), Sse2.ConvertToVector128Double(sum)));
                }
                if (phase == 0)
                    sum = sum.WithElement(0, (float)((double)samples[n - radius] * filters[radius * ratio] + sum.GetElement(0)));
                int offset = (n - radius) * ratio + phase;
                Vector128<double> previous = Sse2.ConvertToVector128Double(Vector64.LoadUnsafe(ref destinationBase, (nuint)offset).ToVector128());
                Vector128<double> result = Sse2.Add(Sse2.Multiply(Sse2.ConvertToVector128Double(sum), scale), Sse2.Multiply(addScale, previous));
                Sse2.ConvertToVector128Single(result).GetLower().StoreUnsafe(ref destinationBase, (nuint)offset);
            }
        }
        return true;
    }

    /// <summary>计算四个独立插值相位，保持各相位的舍入顺序。</summary>
    private static bool TryInterpolateAvx(DspMemory memory, uint descriptor, int ratio, uint input, uint output, int add)
    {
        if (!Avx.IsSupported || ratio is not (8 or 16)) return false;
        const int count = 32;
        int radius = memory.I32(OffsetAddress(descriptor, 4));
        if (radius <= 0) return false;
        uint coefficients = memory.U32(descriptor);
        ReadOnlySpan<byte> inputBytes = memory.Read(input, checked((radius * 2 + count) * 4));
        ReadOnlySpan<byte> coefficientBytes = memory.Read(coefficients, checked((radius * ratio + 1) * 4));
        Span<byte> outputBytes = memory.Write(output, count * ratio * 4);
        // 工作缓冲区发生重叠时，调用方同样使用标量路径。
        if (inputBytes.Overlaps(outputBytes) || coefficientBytes.Overlaps(outputBytes)) return false;
        ReadOnlySpan<float> samples = MemoryMarshal.Cast<byte, float>(inputBytes);
        ReadOnlySpan<float> filters = MemoryMarshal.Cast<byte, float>(coefficientBytes);
        Span<float> destination = MemoryMarshal.Cast<byte, float>(outputBytes);
        ref float filterBase = ref MemoryMarshal.GetReference(filters);
        ref float destinationBase = ref MemoryMarshal.GetReference(destination);
        Vector256<double> scale = Vector256.Create((double)ratio);
        Vector256<double> addScale = Vector256.Create((double)(uint)add);
        // 本次调用中系数保持不变，因此只转换并反转一次，
        // 避免在每个输出位置重复执行这些操作。
        if(radius>64)return false;
        int groups=ratio/4;
        Span<Vector256<double>> leftTable=stackalloc Vector256<double>[radius*groups];
        Span<Vector256<double>> rightTable=stackalloc Vector256<double>[radius*groups];
        for(int j=0;j<radius;j++)for(int group=0;group<groups;group++)
        {
            int phase=group*4,index=j*groups+group;
            leftTable[index]=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref filterBase,(nuint)(j*ratio+phase)));
            Vector128<float> reverse=Vector128.LoadUnsafe(ref filterBase,(nuint)((j+1)*ratio-phase-3));
            rightTable[index]=Avx.ConvertToVector256Double(Sse.Shuffle(reverse,reverse,0x1b));
        }
        for (int n = radius; n < radius + count; n++)
        {
            for (int phase = 0; phase < ratio; phase += 4)
            {
                Vector128<float> sum = Vector128<float>.Zero;
                for (int j = 0; j < radius; j++)
                {
                    Vector256<double> left = Vector256.Create((double)samples[n - j]);
                    Vector256<double> right = Vector256.Create((double)samples[n + j + 1]);
                    // 每个抽头保留两次独立的双精度乘加运算和两次 float 写入。
                    // 横向归约或融合乘加会改变基准输出。
                    sum = Avx.ConvertToVector128Single(Avx.Add(
                        Avx.Multiply(left, leftTable[j*groups+phase/4]), Avx.ConvertToVector256Double(sum)));
                    sum = Avx.ConvertToVector128Single(Avx.Add(
                        Avx.Multiply(right, rightTable[j*groups+phase/4]), Avx.ConvertToVector256Double(sum)));
                }
                if (phase == 0)
                    sum = sum.WithElement(0, (float)((double)samples[n - radius] * filters[radius * ratio] + sum.GetElement(0)));
                int offset = (n - radius) * ratio + phase;
                Vector256<double> previous = Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref destinationBase, (nuint)offset));
                Vector256<double> result = Avx.Add(Avx.Multiply(Avx.ConvertToVector256Double(sum), scale), Avx.Multiply(addScale, previous));
                Avx.ConvertToVector128Single(result).StoreUnsafe(ref destinationBase, (nuint)offset);
            }
        }
        return true;
    }
}
