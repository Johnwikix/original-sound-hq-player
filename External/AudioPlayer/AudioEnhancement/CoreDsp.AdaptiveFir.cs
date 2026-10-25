using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    /// <summary>并行处理独立时间位置，保持两个滤波器各自的累加顺序。</summary>
    private static bool TryAdaptiveFirAvx(DspMemory memory,uint coefficients,uint descriptor,uint input,uint output)
    {
        if(!Avx.IsSupported)return false;
        int block=memory.I32(OffsetAddress(descriptor,4))*2;
        if(block<=0 || block>128 || 128%block!=0 || block%4!=0)return false;
        ReadOnlySpan<byte> inputBytes=memory.Read(input,168*4);
        ReadOnlySpan<byte> coefficientBytes=memory.Read(coefficients,42*4);
        ReadOnlySpan<byte> weightBytes=memory.Read(memory.U32(descriptor),block*2*4);
        Span<byte> outputBytes=memory.Write(output,128*4);
        if(inputBytes.Overlaps(outputBytes)||coefficientBytes.Overlaps(outputBytes)||weightBytes.Overlaps(outputBytes))return false;
        ReadOnlySpan<float> samples=MemoryMarshal.Cast<byte,float>(inputBytes);
        ReadOnlySpan<float> taps=MemoryMarshal.Cast<byte,float>(coefficientBytes);
        ReadOnlySpan<float> weights=MemoryMarshal.Cast<byte,float>(weightBytes);
        Span<float> destination=MemoryMarshal.Cast<byte,float>(outputBytes);
        ref float sampleBase=ref MemoryMarshal.GetReference(samples);
        ref float weightBase=ref MemoryMarshal.GetReference(weights);
        ref float destinationBase=ref MemoryMarshal.GetReference(destination);
        Span<Vector256<double>> first=stackalloc Vector256<double>[21];
        Span<Vector256<double>> second=stackalloc Vector256<double>[21];
        for(int radius=0;radius<=20;radius++)
        {
            first[radius]=Vector256.Create((double)taps[radius*2]);
            second[radius]=Vector256.Create((double)taps[radius*2+1]);
        }
        for(int start=20;start<148;start+=block)for(int j=0;j<block;j+=4)
        {
            int center=start+j;
            Vector256<double> sample=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref sampleBase,(nuint)center));
            Vector128<float> a=Avx.ConvertToVector128Single(Avx.Multiply(sample,first[0]));
            Vector128<float> b=Avx.ConvertToVector128Single(Avx.Multiply(sample,second[0]));
            for(int radius=1;radius<=20;radius++)
            {
                Vector256<double> left=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref sampleBase,(nuint)(center-radius)));
                Vector256<double> right=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref sampleBase,(nuint)(center+radius)));
                Vector256<double> pair=Avx.ConvertToVector256Double(Avx.ConvertToVector128Single(Avx.Add(left,right)));
                a=Avx.ConvertToVector128Single(Avx.Add(Avx.ConvertToVector256Double(a),Avx.Multiply(pair,first[radius])));
                b=Avx.ConvertToVector128Single(Avx.Add(Avx.ConvertToVector256Double(b),Avx.Multiply(pair,second[radius])));
            }
            Vector256<double> weightA=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref weightBase,(nuint)j));
            Vector256<double> weightB=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref weightBase,(nuint)(j+block)));
            Vector256<double> result=Avx.Add(Avx.Multiply(weightA,Avx.ConvertToVector256Double(a)),Avx.Multiply(weightB,Avx.ConvertToVector256Double(b)));
            Avx.ConvertToVector128Single(result).StoreUnsafe(ref destinationBase,(nuint)(center-20));
        }
        return true;
    }
}
