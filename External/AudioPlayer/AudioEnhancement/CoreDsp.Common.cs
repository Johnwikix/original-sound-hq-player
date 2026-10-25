using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    internal static uint OffsetAddress(uint address, int displacement) => unchecked(address + (uint)displacement);

    // 级联递归滤波节，每节包含四个系数和两个历史值。
    internal static void ProcessRecursiveFilterCascade(DspMemory m, uint descriptor, uint input, uint output, uint history)
    {
        uint coefficients=m.U32(descriptor);
        int samples=m.I32(OffsetAddress(descriptor,12))*128, sections=m.I32(OffsetAddress(descriptor,8))/4;
        double scale=m.F32(OffsetAddress(descriptor,4));
        for(int i=0;i<samples;i++)m.F32(OffsetAddress(output,i*4),m.F32(OffsetAddress(input,i*4))*scale);
        for(int section=0;section<sections;section++)
        {
            float s1=m.F32(history),s2=m.F32(OffsetAddress(history,4));
            for(int i=0;i<samples;i++)
            {
                uint p=OffsetAddress(output,i*4);
                float w=(float)((m.F32(p)-(double)s1*m.F32(OffsetAddress(coefficients,8)))-(double)s2*m.F32(OffsetAddress(coefficients,12)));
                if(w>m.F64(CoefficientTables.DenormalLowerBound)&&w<m.F64(CoefficientTables.DenormalUpperBound))w=0;
                m.F32(p,((double)m.F32(coefficients)*s1+w)+(double)m.F32(OffsetAddress(coefficients,4))*s2);
                s2=s1;s1=w;
            }
            m.F32(history,s1);m.F32(OffsetAddress(history,4),s2);
            history+=8;coefficients+=16;
        }
    }

    // 将一个连续存储的声道写入交织输出。
    internal static void WriteInterleavedChannel(DspMemory m,uint source,uint destination,int samples,int format,int channel,int channels)
    {
        if(format is not (2 or 4))return;
        for(int i=0;i<samples;i++)m.U32(OffsetAddress(destination,(i*channels+channel)*4),m.U32(OffsetAddress(source,i*4)));
    }

    // 为各处理阶段分配独立工作缓冲区，并在状态区保存其句柄。
    internal static void InitializeScratchBuffers(DspMemory m,uint outer)
    {
        m.U32(OffsetAddress(outer,DspStateLayout.LeftBandRepairInput),m.AllocateScratch(512));
        m.U32(OffsetAddress(outer,DspStateLayout.LeftBandRepairInput+4),m.AllocateScratch(1344));
        m.U32(OffsetAddress(outer,DspStateLayout.RightBandRepairInput),m.AllocateScratch(512));
        m.U32(OffsetAddress(outer,DspStateLayout.RightBandRepairInput+4),m.AllocateScratch(1344));
        m.U32(OffsetAddress(outer,DspStateLayout.RepairFilterBanks),m.AllocateScratch(336));
        m.U32(OffsetAddress(outer,DspStateLayout.RepairFilterCoefficients),m.AllocateScratch(168));
        m.U32(OffsetAddress(outer,DspStateLayout.ComparisonFilterBank),m.AllocateScratch(336));
        m.U32(OffsetAddress(outer,DspStateLayout.LeftExtensionOutput),m.AllocateScratch(2992));
        m.U32(OffsetAddress(outer,DspStateLayout.LeftExtensionInput),m.AllocateScratch(1232));
        m.U32(OffsetAddress(outer,DspStateLayout.LeftExtensionInput+4),m.AllocateScratch(44));
        m.U32(OffsetAddress(outer,DspStateLayout.RightBandRepairInput+8),m.AllocateScratch(2992));
        m.U32(OffsetAddress(outer,DspStateLayout.RightBandRepairInput+12),m.AllocateScratch(1232));
        m.U32(OffsetAddress(outer,DspStateLayout.RightBandRepairInput+16),m.AllocateScratch(44));
    }

    // 依次使用已启用的频带修复和升频阶段处理浮点帧。
    internal static void ProcessFloatBlock(DspMemory m,uint outer,int frames,uint input,uint output)
    {
        uint s=outer,config=OffsetAddress(outer,DspStateLayout.Configuration);
        int decimation=m.I32(OffsetAddress(outer,DspStateLayout.InterpolationRatio+4));
        if(m.I32(OffsetAddress(outer,DspStateLayout.SynthesisMixMode))!=0)
        {
            int historySamples=m.I32(OffsetAddress(s,DspStateLayout.DirectOutputOffset));
            for(int c=0;c<2;c++)
            {
                uint p=OffsetAddress(m.U32(OffsetAddress(s,DspStateLayout.LeftExtensionOutput+c*DspStateLayout.ChannelStride)),historySamples*4);
                for(int i=0;i<frames;i++)m.U32(OffsetAddress(p,i*4),m.U32(OffsetAddress(input,(i*2+c)*4)));
            }
        }
        int at=0;
        for(int i=0;i<frames;i+=decimation)
        {
            m.U32(OffsetAddress(output,at*8),m.U32(OffsetAddress(input,i*8)));
            m.U32(OffsetAddress(output,at*8+4),m.U32(OffsetAddress(input,i*8+4)));at++;
        }
        if(m.I32(OffsetAddress(outer,DspStateLayout.BandRepairEnabled))!=0)ProcessBandRepair(m,s,128,128,0,output,output,config);
        if(m.I32(OffsetAddress(outer,DspStateLayout.RateExtensionEnabled))!=0)
        {
            int n=m.I32(OffsetAddress(outer,DspStateLayout.InterpolationRatio))*128;
            if(m.I32(OffsetAddress(outer,DspStateLayout.DirectOutputMode))!=0)
            {
                m.Copy(output+0x800,output+0x400,1024);
                ProcessRateExtension(m,s,config,128,n,0,output,output);
                ProcessRateExtension(m,s,config,128,n,0,output+0x800,output+0x800);
            }
            else ProcessRateExtension(m,s,config,128,n,0,output,output);
        }
    }

    // 将整数 PCM 转为浮点数处理，再限幅并转换回整数。
    internal static int ProcessIntegerBlock(DspMemory m,uint outer,uint input,uint output)
    {
        if(outer==0||input==0||output==0)return 2;
        int inputFrames=m.I32(OffsetAddress(outer,DspStateLayout.InputFrames)),outputFrames=m.I32(OffsetAddress(outer,DspStateLayout.OutputFrames));
        if(m.I32(OffsetAddress(outer,DspStateLayout.BypassConversion))==0&&(m.I32(OffsetAddress(outer,DspStateLayout.BandRepairEnabled))==1||m.I32(OffsetAddress(outer,DspStateLayout.RateExtensionEnabled))==1))
        {
            for(int i=0;i<inputFrames*2;i++)m.F32(OffsetAddress(output,i*4),m.I32(OffsetAddress(input,i*4))*m.F64(CoefficientTables.PcmInputScale));
            if(m.I32(OffsetAddress(outer,DspStateLayout.OutputRatio))==2&&m.I32(OffsetAddress(outer,DspStateLayout.InputBlockCount))==2)
            {
                m.Copy(output+0x800,output+0x400,1024);
                ProcessFloatBlock(m,outer,128,output,output);
                ProcessFloatBlock(m,outer,128,output+0x800,output+0x800);
            }
            else
            {
                uint pin=output,pout=output;
                for(int i=0;i<m.I32(OffsetAddress(outer,DspStateLayout.InputBlockCount));i++)
                {
                    int n=m.I32(OffsetAddress(outer,DspStateLayout.ExcitationHistoryRadius+4));ProcessFloatBlock(m,outer,n,pin,pout);
                    pin=OffsetAddress(pin,n*8);pout=OffsetAddress(pout,m.I32(OffsetAddress(outer,DspStateLayout.ExcitationHistoryRadius+8))*8);
                }
            }
            double maximum=m.F64(CoefficientTables.PcmUpperBound),scale=m.F64(CoefficientTables.PcmOutputScale);
            for(int i=0;i<outputFrames*2;i++)
            {
                uint p=OffsetAddress(output,i*4);double v=Math.Clamp((double)m.F32(p),-1,maximum);
                m.I32(p,unchecked((int)(long)(v*scale)));
            }
        }
        else m.Copy(output,input,inputFrames*8);
        return 0;
    }
}
