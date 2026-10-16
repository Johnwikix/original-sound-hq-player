using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    /// <summary>利用缓存的对称样本对，将固定原型与动态增益进行卷积。</summary>
    private static bool TrySynthesizePrototype(DspMemory memory,uint state,uint prototype,int length)
    {
        if(!BitConverter.IsLittleEndian||!CoefficientStore.IsCoefficient(prototype)||length<=0)return false;
        for(int channel=0;channel<2;channel++)
        {
            uint address=memory.U32(OffsetAddress(state,DspStateLayout.SynthesisDescriptors+channel*8));
            if(memory.Read(OffsetAddress(state,DspStateLayout.SynthesisCoefficients+channel*100),100).Overlaps(memory.Write(address,checked(length*4))))return false;
        }
        ReadOnlySpan<float> pairs=memory.PrototypePairs(prototype,length);
        for(int channel=0;channel<2;channel++)
        {
            ReadOnlySpan<float> coefficients=memory.Floats(OffsetAddress(state,DspStateLayout.SynthesisCoefficients+channel*100),25);
            Span<float> output=memory.WritableFloats(memory.U32(OffsetAddress(state,DspStateLayout.SynthesisDescriptors+channel*8)),length);
            int i=0;
            if(Avx.IsSupported)
            {
                ref float source=ref MemoryMarshal.GetReference(pairs);
                ref float destination=ref MemoryMarshal.GetReference(output);
                for(;i<=length-4;i+=4)
                {
                    Vector128<float> sum=Avx.ConvertToVector128Single(Avx.Multiply(
                        Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref source,(nuint)i)),Vector256.Create((double)coefficients[0])));
                    for(int radius=1;radius<=24;radius++)
                    {
                        Vector256<double> pair=Avx.ConvertToVector256Double(Vector128.LoadUnsafe(ref source,(nuint)(radius*length+i)));
                        sum=Avx.ConvertToVector128Single(Avx.Add(Avx.Multiply(pair,Vector256.Create((double)coefficients[radius])),Avx.ConvertToVector256Double(sum)));
                    }
                    sum.StoreUnsafe(ref destination,(nuint)i);
                }
            }
            else if(Sse2.IsSupported)
            {
                ref float source=ref MemoryMarshal.GetReference(pairs);
                ref float destination=ref MemoryMarshal.GetReference(output);
                for(;i<=length-2;i+=2)
                {
                    Vector128<float> sum=Sse2.ConvertToVector128Single(Sse2.Multiply(
                        Sse2.ConvertToVector128Double(Vector64.LoadUnsafe(ref source,(nuint)i).ToVector128()),Vector128.Create((double)coefficients[0])));
                    for(int radius=1;radius<=24;radius++)
                    {
                        Vector128<double> pair=Sse2.ConvertToVector128Double(Vector64.LoadUnsafe(ref source,(nuint)(radius*length+i)).ToVector128());
                        sum=Sse2.ConvertToVector128Single(Sse2.Add(Sse2.Multiply(pair,Vector128.Create((double)coefficients[radius])),Sse2.ConvertToVector128Double(sum)));
                    }
                    sum.GetLower().StoreUnsafe(ref destination,(nuint)i);
                }
            }
            for(;i<length;i++)
            {
                float sum=(float)(pairs[i]*(double)coefficients[0]);
                for(int radius=1;radius<=24;radius++)sum=(float)(pairs[radius*length+i]*(double)coefficients[radius]+sum);
                output[i]=sum;
            }
        }
        return true;
    }
}
