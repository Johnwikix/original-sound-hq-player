using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

internal static partial class CoreDsp
{
    // 解码紧凑参数、配置滤波器组并重置各声道的处理历史。
    internal static void InitializeProcessingState(DspMemory m, uint s, uint parameters, int channels, int boundary, uint emphasisPointer, int reset)
    {
        m.I32(OffsetAddress(s,DspStateLayout.RandomSeed),0x163FB);
        if(reset!=0)
        {
            m.Read(parameters,512).CopyTo(m.Write(OffsetAddress(s,DspStateLayout.PackedParameters),512));
            uint reader=OffsetAddress(s,DspStateLayout.PackedReader), decoded=OffsetAddress(s,DspStateLayout.PackedParameters);
            m.U32(reader,OffsetAddress(decoded,4)); m.U32(OffsetAddress(reader,4),m.U32(decoded)); m.I32(OffsetAddress(reader,8),0);
            ReadPackedMatrix(m,reader,4,8,OffsetAddress(s,DspStateLayout.BandPredictors));
            ReadPackedMatrix(m,reader,1,8,OffsetAddress(s,DspStateLayout.ComparisonPredictors));
            float level=ReadPackedScaledValue(m,reader,1,false);
            if(m.F32(OffsetAddress(s,DspStateLayout.LevelOffset))==0) m.F32(OffsetAddress(s,DspStateLayout.LevelOffset),level);
            m.F32(OffsetAddress(s,DspStateLayout.RepairGainLimit),ReadPackedInteger(m,reader)*m.F64(CoefficientTables.TenthsScale));
            m.I32(OffsetAddress(s,DspStateLayout.MeasurementFloorEnabled),ReadPackedInteger(m,reader));
            int index=ReadPackedInteger(m,reader);
            for(int i=0;i<8;i++)m.F32(OffsetAddress(s,DspStateLayout.BandWeights+i*4),(double)ReadPackedInteger(m,reader)*m.F32(OffsetAddress(CoefficientTables.BandWeightScales,index*4)));
            m.I32(OffsetAddress(s,DspStateLayout.PredictionAdjustmentMode),ReadPackedInteger(m,reader));
            m.I32(OffsetAddress(s,DspStateLayout.ComparisonMode),ReadPackedInteger(m,reader));
            if(m.I32(OffsetAddress(s,DspStateLayout.DcrEnabled))!=0)m.I32(OffsetAddress(s,DspStateLayout.ComparisonMode),1);
            m.F32(OffsetAddress(s,DspStateLayout.EmphasisAmount),ReadPackedInteger(m,reader));
            if(m.I32(emphasisPointer)==0)m.F32(OffsetAddress(s,DspStateLayout.EmphasisAmount),0f);
            m.I32(OffsetAddress(s,DspStateLayout.ReservedControl),ReadPackedInteger(m,reader));
            ReadEmphasisControls(m,OffsetAddress(s,DspStateLayout.EmphasisControls),reader,m.F32(OffsetAddress(s,DspStateLayout.EmphasisAmount)),m.U32(OffsetAddress(s,DspStateLayout.InputRate)),m.F32(OffsetAddress(s,DspStateLayout.GainCompensation)));
            InitializeEmphasisHistory(m,2,OffsetAddress(s,DspStateLayout.EmphasisControls),OffsetAddress(s,DspStateLayout.EmphasisHistory),m.F32(OffsetAddress(s,DspStateLayout.LevelOffset)));
            ReadPackedInteger(m,reader,4);
            for(int k=9;k<16;k++)
            {
                ReadPackedMatrix(m,reader,4,16-k,k==boundary?OffsetAddress(s,DspStateLayout.BandPredictors):m.U32(OffsetAddress(s,DspStateLayout.LeftExtensionInput)));
                ReadPackedMatrix(m,reader,1,16-k,k==boundary?OffsetAddress(s,DspStateLayout.ComparisonPredictors):m.U32(OffsetAddress(s,DspStateLayout.LeftExtensionInput)));
            }
            for(int i=0;i<8;i++)
            {
                uint row=OffsetAddress(s,DspStateLayout.BandPredictors+i*20), compare=OffsetAddress(s,DspStateLayout.ComparisonPredictors+i*8);
                float sum=m.F32(OffsetAddress(row,4))+0f; sum+=m.F32(OffsetAddress(row,8)); sum+=m.F32(OffsetAddress(row,12));sum+=m.F32(OffsetAddress(row,16));
                m.F32(row,((double)sum-1)*m.F32(OffsetAddress(s,DspStateLayout.LevelOffset))+m.F32(row));
                m.F32(compare,((double)m.F32(OffsetAddress(compare,4))-1)*m.F32(OffsetAddress(s,DspStateLayout.LevelOffset))+m.F32(compare));
            }
            m.F32(OffsetAddress(s,DspStateLayout.PredictionBias),ReadPackedInteger(m,reader,1,true)*m.F64(CoefficientTables.TenthsScale));
            m.F32(OffsetAddress(s,DspStateLayout.MeasurementBias),ReadPackedInteger(m,reader,1,true)*m.F64(CoefficientTables.TenthsScale));
            float dbFactor=(float)Math.Pow(10,ReadPackedInteger(m,reader,1,true)/20.0);
            m.F32(OffsetAddress(s,DspStateLayout.ExtensionGain),(double)dbFactor*m.F32(OffsetAddress(s,DspStateLayout.ExtensionGain)));
            m.F32(OffsetAddress(s,DspStateLayout.ExtensionGainLimit),ReadPackedInteger(m,reader)*m.F64(CoefficientTables.TenthsScale));
            int rate=m.I32(OffsetAddress(s,DspStateLayout.InputRate)), factor=m.I32(OffsetAddress(s,DspStateLayout.InterpolationRatio));
            int selected=(factor==2?0:factor==4?2:-100)+(rate==44100?0:rate==48000?1:-100);
            for(int i=0;i<4;i++)ReadPackedMatrix(m,reader,3,4,i==selected?OffsetAddress(s,DspStateLayout.ExtensionPredictors):m.U32(OffsetAddress(s,DspStateLayout.LeftExtensionInput)));
            for(int i=0;i<16;i++)if(m.F32(OffsetAddress(s,DspStateLayout.ExtensionPredictors+i*4))==-32f)m.F32(OffsetAddress(s,DspStateLayout.ExtensionPredictors+i*4),-200f);
            for(int i=0;i<4;i++)
            {
                uint row=OffsetAddress(s,DspStateLayout.ExtensionPredictors+i*16);
                float sum=m.F32(OffsetAddress(row,4))+0f;sum+=m.F32(OffsetAddress(row,8));sum+=m.F32(OffsetAddress(row,12));
                m.F32(row,((double)sum-1)*m.F32(OffsetAddress(s,DspStateLayout.LevelOffset))+m.F32(row));
            }
            int floor=ReadPackedInteger(m,reader,1,true);
            float excitation=floor < -120?0f:(float)((float)Math.Pow(10,((double)floor-m.F32(OffsetAddress(s,DspStateLayout.LevelOffset)))/20)*m.F64(CoefficientTables.ExcitationScale));
            m.F32(OffsetAddress(s,DspStateLayout.ExcitationGain),excitation);
            float addend=(float)(ReadPackedInteger(m,reader,1,true)*m.F64(CoefficientTables.TenthsScale));
            if(m.I32(OffsetAddress(s,DspStateLayout.SynthesisMixMode))!=0)m.F32(OffsetAddress(s,DspStateLayout.ExtensionPredictors),m.F32(OffsetAddress(s,DspStateLayout.ExtensionPredictors))+addend);
            m.I32(OffsetAddress(s,DspStateLayout.SynthesisMode),ReadPackedInteger(m,reader));
            for(int c=0;c<2;c++)m.Clear(OffsetAddress(s,DspStateLayout.LeftSynthesisTaps+c*DspStateLayout.ChannelStride),0xC1*4);
        }
        uint spec=OffsetAddress(s,DspStateLayout.SynthesisSpecification);
        m.Clear(spec,0x34);m.I32(OffsetAddress(spec,8),boundary+4);ConfigureSynthesisFilterBank(m,boundary+4,spec);BuildModulatedSynthesisFilters(m,m.U32(OffsetAddress(s,DspStateLayout.ComparisonFilterBank)),spec);
        m.Clear(spec,0x34);m.I32(OffsetAddress(spec,8),boundary);ConfigureSynthesisFilterBank(m,boundary,spec);BuildModulatedSynthesisFilters(m,m.U32(OffsetAddress(s,DspStateLayout.RepairFilterBanks)),spec);
        ConfigureBandRepairAnalysisFilters(m,s);ConfigureRateExtensionFilters(m,s);m.U32(OffsetAddress(s,DspStateLayout.EmphasisEnabled),m.U32(emphasisPointer));
        for(int c=0;c<channels;c++)
        {
            uint ch=OffsetAddress(s,DspStateLayout.Channels+c*DspStateLayout.ChannelStride),p=OffsetAddress(ch,8);
            m.Clear(m.U32(OffsetAddress(p,-8)),0x80*4);m.Clear(m.U32(p),0x2EC*4);
            m.Clear(OffsetAddress(ch,1280),8*4);m.Clear(OffsetAddress(ch,1312),8*4);m.Clear(OffsetAddress(ch,1344),8*4);m.Clear(OffsetAddress(ch,1376),2*4);
            m.Clear(m.U32(OffsetAddress(p,-4)),0x150*4);m.Clear(OffsetAddress(p,12),2*4);m.Clear(m.U32(OffsetAddress(p,4)),0x134*4);
            m.Clear(OffsetAddress(p,56),0x3C*4);m.Clear(OffsetAddress(p,296),0x24*4);m.Clear(OffsetAddress(p,440),4*4);
            m.Clear(OffsetAddress(p,1376),0x9A*4);m.Clear(OffsetAddress(p,2000),2*4);
            for(int i=-0x36;i<=-0xF;i++)m.F32(OffsetAddress(p,i*4+2224),-120f);
        }
    }

    // 保留紧凑参数读取器的字缓存和算术右移行为。
    private static int ReadPackedInteger(DspMemory m,uint reader,int width=1,bool signed=false)
    {
        uint result=0;
        for(int i=0;i<width;i++)
        {
            if(m.I32(OffsetAddress(reader,8))==4){uint ptr=m.U32(reader);m.U32(OffsetAddress(reader,4),m.U32(ptr));m.U32(reader,OffsetAddress(ptr,4));m.I32(OffsetAddress(reader,8),0);}
            uint word=m.U32(OffsetAddress(reader,4));m.I32(OffsetAddress(reader,8),m.I32(OffsetAddress(reader,8))+1);
            m.I32(OffsetAddress(reader,4),unchecked((int)word)>>8);result=unchecked(result+((word&255)<<(i*8&31)));
        }
        if(signed&&(result&(0x80u<<((width*8-8)&31)))!=0)result=unchecked(0u-(~(result-1)&255));
        return unchecked((int)result);
    }

    private static float ReadPackedScaledValue(DspMemory m,uint reader,int width,bool signed)
    {
        int exponent=ReadPackedInteger(m,reader);return (float)((double)ReadPackedInteger(m,reader,width,signed)*m.F32(OffsetAddress(CoefficientTables.ExponentScales,exponent*4)));
    }

    private static void ReadPackedMatrix(DspMemory m,uint reader,int features,int rows,uint destination)
    {
        Span<int> exp=stackalloc int[5];for(int c=0;c<=features;c++)exp[c]=ReadPackedInteger(m,reader);
        for(int r=0;r<rows;r++)for(int c=0;c<=features;c++)m.F32(OffsetAddress(destination,(r*(features+1)+c)*4),(double)ReadPackedInteger(m,reader,1,true)*m.F32(OffsetAddress(CoefficientTables.ExponentScales,exp[c]*4)));
    }

    // 读取预加重参数，并根据分贝阈值计算线性增益。
    internal static void ReadEmphasisControls(DspMemory m,uint p,uint reader,float emphasis,uint rate,float overrideValue)
    {
        ReadPackedInteger(m,reader);m.F32(p,(float)rate);
        for(int i=1;i<=5;i++)m.F32(OffsetAddress(p,i*4),ReadPackedScaledValue(m,reader,1,false));
        for(int i=6;i<=8;i++){float v=ReadPackedScaledValue(m,reader,2,false);m.F32(OffsetAddress(p,i*4),i==8?(double)emphasis-v:v);}
        m.F32(OffsetAddress(p,0x24),(double)emphasis-ReadPackedScaledValue(m,reader,1,true));
        for(int i=10;i<=12;i++){float v=ReadPackedScaledValue(m,reader,1,true);m.F32(OffsetAddress(p,i*4),i==10&&overrideValue!=0?overrideValue:v);}
        for(int i=13;i<=18;i++)m.F32(OffsetAddress(p,i*4),ReadPackedScaledValue(m,reader,1,false));
        float clamp=ReadPackedScaledValue(m,reader,1,true);m.F32(OffsetAddress(p,0x4C),clamp);
        if(m.F32(OffsetAddress(p,0x24)) < -clamp)m.F32(OffsetAddress(p,0x24),-clamp);
        m.F32(OffsetAddress(p,0x50),ReadPackedScaledValue(m,reader,1,true));
        if(emphasis==0)for(int i=10;i<=13;i++)m.F32(OffsetAddress(p,i*4),0f);
        float gain=(float)Math.Pow(10,m.F32(OffsetAddress(p,0x24))/20.0);m.F32(OffsetAddress(p,0x3C),gain);m.F32(OffsetAddress(p,0x44),gain);
        m.F32(OffsetAddress(p,0x40),Math.Pow(10,((double)m.F32(OffsetAddress(p,0x24))-m.F32(OffsetAddress(p,0x20)))/20.0));
    }

    // 根据配置电平初始化包络能量，并计算平滑系数。
    internal static void InitializeEmphasisHistory(DspMemory m,int channels,uint p,uint destination,float level)
    {
        m.I32(destination,0);float scale=(float)Math.Pow(10,-(double)level/20);
        for(int c=0;c<channels;c++)for(int b=0;b<3;b++){double gain=m.F32(OffsetAddress(p,0x3C+b*4));m.F32(OffsetAddress(destination,4+c*4+b*8),gain*gain*scale);}
        for(int i=0;i<5;i++)m.F32(OffsetAddress(destination,0x70+i*4),Math.Pow(0.5,1.0/((double)m.F32(OffsetAddress(p,4+i*4))*m.F32(p))));
        for(int i=0;i<channels*5;i++){m.F32(OffsetAddress(destination,0x1C+i*4),0f);m.F32(OffsetAddress(destination,0x44+i*4),0f);}
        m.I32(OffsetAddress(destination,0x84),unchecked((int)((double)m.F32(OffsetAddress(p,0x38))*m.F32(p))));
    }

    // 配置四个调制合成滤波器。
    internal static void ConfigureSynthesisFilterBank(DspMemory m,int boundary,uint p)
    {
        m.U32(p,CoefficientTables.CrossfadeWindow);m.I32(OffsetAddress(p,4),4);m.I32(OffsetAddress(p,12),Math.Min(16-boundary,8));m.I32(OffsetAddress(p,16),4);
        for(int i=0;i<8;i++)m.I32(OffsetAddress(p,20+i*4),i%4);
    }

    // 对 21 个原型抽头分别进行四路余弦调制，并按抽头逆序存储。
    internal static void BuildModulatedSynthesisFilters(DspMemory m,uint destination,uint spec)
    {
        int count=m.I32(OffsetAddress(spec,16)),boundary=m.I32(OffsetAddress(spec,8));Span<float> angles=stackalloc float[4];
        for(int i=0;i<count;i++)angles[i]=(float)((2*(boundary+i)-2*count+1)*m.F64(CoefficientTables.ModulationAngleStep)*m.F64(CoefficientTables.ModulationAngleScale));
        m.Clear(destination,0x54*4);
        for(int tap=0;tap<21;tap++)for(int i=0;i<count;i++)m.F32(OffsetAddress(destination,(20-tap)*16+i*4),2.0*m.F32(OffsetAddress(CoefficientTables.SynthesisPrototype,tap*4))*Math.Cos((double)angles[i]*(tap-20)));
    }

    // 为选定的频带修复边界选择固定分析滤波器组。
    internal static void ConfigureBandRepairAnalysisFilters(DspMemory m,uint s)
    {
        int k=m.I32(OffsetAddress(s,DspStateLayout.SynthesisSpecification+8));
        m.U32(OffsetAddress(s,DspStateLayout.LowBandAnalysis),OffsetAddress(CoefficientTables.LowBandAnalysisBanks,(k-8)*0x40));m.U32(OffsetAddress(s,DspStateLayout.LowBandAnalysis+4),m.U32(OffsetAddress(CoefficientTables.LowBandAnalysisScales,k*4)));
        m.I32(OffsetAddress(s,DspStateLayout.LowBandAnalysis+8),16);m.I32(OffsetAddress(s,DspStateLayout.LowBandAnalysis+12),1);
        m.U32(OffsetAddress(s,DspStateLayout.HighBandAnalysis),OffsetAddress(CoefficientTables.HighBandAnalysisBanks,(k-8)*0x40));m.U32(OffsetAddress(s,DspStateLayout.HighBandAnalysis+4),m.U32(OffsetAddress(CoefficientTables.HighBandAnalysisScales,k*4)));
        m.I32(OffsetAddress(s,DspStateLayout.HighBandAnalysis+8),16);m.I32(OffsetAddress(s,DspStateLayout.HighBandAnalysis+12),1);m.U32(OffsetAddress(s,DspStateLayout.RepairOutputCascade),CoefficientTables.RepairRecursiveCoefficients);
        m.U32(OffsetAddress(s,DspStateLayout.RepairOutputCascade+4),0x3F44E2D9);m.I32(OffsetAddress(s,DspStateLayout.RepairOutputCascade+8),4);m.I32(OffsetAddress(s,DspStateLayout.RepairOutputCascade+12),1);
    }

    // 连接二倍或四倍插值滤波器组及声道历史缓冲区。
    internal static void ConfigureRateExtensionFilters(DspMemory m,uint s)
    {
        int factor=m.I32(OffsetAddress(s,DspStateLayout.InterpolationRatio)),rate=m.I32(OffsetAddress(s,DspStateLayout.InputRate));
        if(factor is 2 or 4)
        {
            bool x2=factor==2; m.I32(OffsetAddress(s,DspStateLayout.InputHistoryRadius),x2?16:12);
            if(rate==44100)m.U32(OffsetAddress(s,DspStateLayout.InputInterpolationFilter),x2?CoefficientTables.InputInterpolation44Double:CoefficientTables.InputInterpolation44Quadruple);
            else if(rate==48000)m.U32(OffsetAddress(s,DspStateLayout.InputInterpolationFilter),x2?CoefficientTables.InputInterpolation48Double:CoefficientTables.InputInterpolation48Quadruple);
            m.I32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors+4),x2?14:12);m.U32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors),OffsetAddress(s,DspStateLayout.LeftSynthesisTaps));m.I32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors+12),x2?14:12);
            m.I32(OffsetAddress(s,DspStateLayout.LeftExcitationRadius),2);m.U32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors+16),x2?CoefficientTables.ExcitationInterpolationDouble:CoefficientTables.ExcitationInterpolationQuadruple);m.U32(OffsetAddress(s,DspStateLayout.SynthesisDescriptors+8),OffsetAddress(s,DspStateLayout.RightSynthesisTaps));
            m.I32(OffsetAddress(s,DspStateLayout.LeftExcitationRadius+8),2);m.U32(OffsetAddress(s,DspStateLayout.LeftExcitationRadius+4),x2?CoefficientTables.ExcitationInterpolationDouble:CoefficientTables.ExcitationInterpolationQuadruple);
            if(rate==44100)m.U32(OffsetAddress(s,DspStateLayout.ExtensionPrototype),x2?CoefficientTables.ExtensionPrototype44Double:CoefficientTables.ExtensionPrototype44Quadruple);
            else if(rate==48000)m.U32(OffsetAddress(s,DspStateLayout.ExtensionPrototype),x2?CoefficientTables.ExtensionPrototype48Double:CoefficientTables.ExtensionPrototype48Quadruple);
            m.I32(OffsetAddress(s,DspStateLayout.ExtensionPrototypeLength),x2?0x54:0xA4);m.U32(OffsetAddress(s,DspStateLayout.ExtensionSynthesisBasis),x2?CoefficientTables.ExtensionBasisDouble:CoefficientTables.ExtensionBasisQuadruple);
        }
        m.U32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisBank),CoefficientTables.ExtensionAnalysisBank);m.U32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisCascade),CoefficientTables.ExtensionRecursiveCoefficients);m.U32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisCascade+4),0x3F44E2D9);
        m.I32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisCascade+8),4);m.I32(OffsetAddress(s,DspStateLayout.ExtensionAnalysisCascade+12),1);
    }
}
