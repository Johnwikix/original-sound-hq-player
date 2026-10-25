using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    // 为基础采样率输入配置处理阶段、采样率系列、紧凑参数和块大小。
    internal static void Initialize(DspMemory m,uint outer,ReadOnlySpan<int> fields)
    {
        if(fields.Length!=12)throw new ArgumentException("Expected twelve DSP configuration integers.",nameof(fields));
        int mode=fields[0],input=fields[1],output=fields[2],selector=fields[3],original=fields[4];
        if(input is not (44100 or 48000)||output%input!=0||output/input is not (1 or 2 or 4))
            throw new NotSupportedException("This processor accepts 44.1/48 kHz input and matching 1×/2×/4× output families.");
        if(mode is <0 or >3||selector is <0 or >17||fields[5] is <0 or >1||fields[6] is <0 or >1020||
           (fields[7]!=0&&fields[7] is not (>=8 and <=16))||fields[8] is <0 or >2000||fields[9] is <0 or >1||
           fields[10] is <0 or >150||fields[11] is <-601 or >60)
            throw new ArgumentOutOfRangeException(nameof(fields),"A DSP configuration field is out of range.");
        if(mode==1&&input!=output)throw new ArgumentException("Band-repair-only mode requires equal base rates.",nameof(fields));
        int inputIndex=RateIndex(m,input),outputIndex=RateIndex(m,output),originalIndex=RateIndex(m,original);
        if(originalIndex<0)throw new ArgumentException("The original sampling rate is absent from the sample-rate table.",nameof(fields));
        m.Clear(outer,DspStateLayout.ByteLength);m.I32(OffsetAddress(outer,DspStateLayout.ChannelCount),2);uint c=OffsetAddress(outer,DspStateLayout.Configuration),s=outer;
        int[] config=[2,mode==0?0:10,selector,originalIndex,inputIndex,outputIndex,fields[5],4,
                      fields[6]/4*4,mode,fields[7],fields[8],fields[9],fields[10],fields[11]];
        for(int i=0;i<config.Length;i++)m.I32(OffsetAddress(c,i*4),config[i]);
        int stage1=SelectBand(m,c);
        int ratio=output/input,stage2=1;
        if(mode==0){stage1=0;stage2=0;}else if(mode==2)stage1=0;else if(mode==1)stage2=0;
        if(ratio==1)stage2=0;
        if(fields[11]==-601){stage1=0;stage2=0;}
        m.I32(OffsetAddress(outer,DspStateLayout.BandRepairEnabled),stage1);m.I32(OffsetAddress(outer,DspStateLayout.RateExtensionEnabled),stage2);
        m.F32(OffsetAddress(outer,DspStateLayout.LevelOffset),fields[8]*m.F64(CoefficientTables.TenthsScale));m.I32(OffsetAddress(outer,DspStateLayout.DcrEnabled),fields[9]);
        m.F32(OffsetAddress(outer,DspStateLayout.GainCompensation),fields[10]*m.F64(CoefficientTables.TenthsScale));m.F32(OffsetAddress(outer,DspStateLayout.GainCompensation+4),fields[11]*m.F64(CoefficientTables.TenthsScale));
        m.I32(OffsetAddress(outer,DspStateLayout.SessionInputRate),input);m.I32(OffsetAddress(outer,DspStateLayout.SessionInputRate+4),output);m.I32(OffsetAddress(outer,DspStateLayout.InputRate),input);
        m.I32(OffsetAddress(outer,DspStateLayout.InterpolationRatio),ratio);m.I32(OffsetAddress(outer,DspStateLayout.InterpolationRatio+4),1);m.I32(OffsetAddress(outer,DspStateLayout.DirectOutputMode),0);
        if(ratio>1){m.I32(OffsetAddress(outer,DspStateLayout.AnalysisHistoryRadius),ratio==2?14:12);m.I32(OffsetAddress(outer,DspStateLayout.ExcitationHistoryRadius),2);}
        m.I32(OffsetAddress(outer,DspStateLayout.ExcitationHistoryRadius+4),128);m.I32(OffsetAddress(outer,DspStateLayout.ExcitationHistoryRadius+8),128*ratio);m.I32(OffsetAddress(outer,DspStateLayout.SynthesisMixMode),0);
        m.F32(OffsetAddress(outer,DspStateLayout.RepairLevelOffset),stage1==1?m.F32(OffsetAddress(outer,DspStateLayout.GainCompensation+4)):0);
        m.F32(OffsetAddress(outer,DspStateLayout.ExtensionGain),stage1==1||stage2==0?1:Math.Pow(10,m.F32(OffsetAddress(outer,DspStateLayout.GainCompensation+4))/20.0));
        m.I32(OffsetAddress(outer,DspStateLayout.EnergyReferenceIndex),3);m.I32(OffsetAddress(outer,DspStateLayout.AnalysisBlockFrames),128);
        m.I32(OffsetAddress(outer,DspStateLayout.DirectOutputOffset),ratio==2?102:ratio==4?188:0);m.I32(OffsetAddress(outer,DspStateLayout.RetainedOutputSamples),0);
        int delay=stage1==1?30:0;if(stage2==1)delay=ratio==2?delay*2+134:delay*4+236;
        m.I32(OffsetAddress(outer,DspStateLayout.Latency),delay);
        uint packed=OffsetAddress(outer,DspStateLayout.PackedConfiguration);m.Clear(packed,32);
        m.U8(packed,2);m.U8(packed+1,(byte)((inputIndex<<4)|2));m.U8(packed+2,(byte)outputIndex);
        m.U8(packed+3,(byte)((fields[5]<<4)|originalIndex));m.U8(packed+4,(byte)(fields[6]>>2));
        m.U8(packed+5,(byte)((fields[9]<<4)|mode));m.U8(packed+6,(byte)selector);
        m.I32(packed+8,fields[7]);m.I32(packed+12,fields[8]);m.I32(packed+16,fields[10]);m.I32(packed+20,fields[11]);
        InitializeScratchBuffers(m,outer);
        InitializeProcessingState(m,s,CoefficientTables.PackedParameters,2,m.I32(OffsetAddress(outer,DspStateLayout.BandBoundary)),OffsetAddress(outer,DspStateLayout.EmphasisConfiguration),1);
        m.I32(OffsetAddress(outer,DspStateLayout.InputBlockCount),4/ratio);m.I32(OffsetAddress(outer,DspStateLayout.InputFrames),512/ratio);m.I32(OffsetAddress(outer,DspStateLayout.OutputFrames),512);
        m.I32(OffsetAddress(outer,DspStateLayout.OutputRatio),ratio);m.I32(OffsetAddress(outer,DspStateLayout.BypassConversion),0);
    }

    private static int RateIndex(DspMemory m,int rate)
    {
        for(int i=0;i<16;i++)if(m.I32(OffsetAddress(CoefficientTables.SampleRates,i*4))==rate)return i;
        return -1;
    }

    // 根据音源、码率和采样率表选择频带边界。
    private static int SelectBand(DspMemory m,uint c)
    {
        int k=16,source=m.I32(c+12),channels=m.I32(c),vbr=m.I32(c+24),selector=m.I32(c+8);
        uint table=0;
        if(m.I32(c+4)!=0)
        {
            if(source is >=4 and <=7)table=channels==1?(vbr!=0?CoefficientTables.MonoVbrSelection:CoefficientTables.MonoCbrSelection):(vbr!=0?CoefficientTables.StereoVbrSelection:CoefficientTables.StereoCbrSelection);
            else if(source==8)table=channels==1?(vbr!=0?CoefficientTables.MonoVbrSelection:CoefficientTables.AlternateMonoCbrSelection):(vbr!=0?CoefficientTables.StereoVbrSelection:CoefficientTables.AlternateStereoCbrSelection);
        }
        if(table==0){m.I32(c+60,16);return 0;}
        uint rows=m.U32(OffsetAddress(table,selector*8));int count=m.I16(OffsetAddress(table,selector*8+4));
        if(rows==0){m.I32(c+60,16);return 0;}
        int i=0;
        for(;i<count;i++)if(unchecked((uint)m.I16(OffsetAddress(rows,i*4+2)))<=m.U32(c+32)){k=m.I16(OffsetAddress(rows,i*4));break;}
        if(m.I32(c+40)!=0)k=m.I32(c+40);
        if(source is >=0 and <=3)k=16;
        else if(source is 4 or 5){if(selector is 0 or 1)k=16;else if(k is >=9 and <=15)k=8;}
        else if(source==6){if(selector is 0 or 1)k=16;else if(k is >=12 and <=15)k=11;}
        if(i!=count&&k!=16)
        {
            // 边界修正使用 config+0x10 处的输入采样率索引。
            if(m.I32(c+16) is 8 or 11 or 15&&m.I32(c+40)==0)k=Math.Max(8,k-1);
            m.I32(c+60,k);return 1;
        }
        m.I32(c+60,16);return 0;
    }
}
