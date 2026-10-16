using AudioPlayer.AudioEnhancement.Coefficients;

namespace AudioPlayer.AudioEnhancement;

/// <summary>实现频带修复、升频及其共用的 DSP 运算。</summary>
internal static partial class CoreDsp
{
    // 分析各声道、预测缺失频带并混入合成的修复信号。
    internal static void ProcessBandRepair(DspMemory m, uint state, int inputCount, int outputCount, int outputOffset, uint input, uint output, uint config)
    {
        int channels = m.I32(config);
        m.U32(OffsetAddress(state,DspStateLayout.EmphasisEnabled), m.U32(OffsetAddress(config,4)));
        for (int c=0;c<channels;c++)
        {
            uint channel = OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride), buffer=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput));
            for (int i=0;i<inputCount;i++) m.U32(OffsetAddress(buffer,i*4),m.U32(OffsetAddress(input,(i*channels+c)*4)));
            m.Clear(OffsetAddress(buffer,inputCount*4),(128-inputCount)*4);
        }
        if (m.I32(OffsetAddress(state,DspStateLayout.SynthesisMode)) != 2)
        {
            for(int c=0;c<channels;c++)
            {
                uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride), buffer=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput)), work=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairAnalysis));
                ProcessRecursiveFilterCascade(m,OffsetAddress(state,DspStateLayout.HighBandAnalysis),buffer,OffsetAddress(work,0x340),OffsetAddress(channel,ChannelStateLayout.HighBandHistory));
                MeasureBandRepairEnergies(m,m.U32(OffsetAddress(state,DspStateLayout.ComparisonFilterBank)),OffsetAddress(work,0x2A0),OffsetAddress(channel,ChannelStateLayout.ComparisonEnergies));
            }
            for(int c=0;c<channels;c++)
            {
                uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride);
                ProcessRecursiveFilterCascade(m,OffsetAddress(state,DspStateLayout.LowBandAnalysis),m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput)),OffsetAddress(channel,ChannelStateLayout.RepairSignal),OffsetAddress(channel,ChannelStateLayout.LowBandHistory));
            }
            Span<uint> pointers=stackalloc uint[2];
            pointers[0]=OffsetAddress(state,DspStateLayout.LeftRepairSignal); pointers[1]=OffsetAddress(state,channels==1 ? DspStateLayout.LeftRepairSignal : DspStateLayout.RightRepairSignal);
            ApplyAdaptiveEmphasis(m,channels,inputCount,OffsetAddress(state,DspStateLayout.EmphasisControls),OffsetAddress(state,DspStateLayout.EmphasisHistory),m.F32(OffsetAddress(state,DspStateLayout.LevelOffset)),pointers,pointers);
            for(int c=0;c<channels;c++)
            {
                uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride),work=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairAnalysis));
                ProcessRecursiveFilterCascade(m,OffsetAddress(state,DspStateLayout.RepairOutputCascade),OffsetAddress(channel,ChannelStateLayout.RepairSignal),OffsetAddress(work,0xA0),OffsetAddress(channel,ChannelStateLayout.EmphasisFilterHistory));
            }
            for(int c=0;c<channels;c++)
            {
                uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride);
                for(int i=6;i>=0;i--) m.Copy(OffsetAddress(channel,ChannelStateLayout.BandEnergyHistoryTail-i*20+20),OffsetAddress(channel,ChannelStateLayout.BandEnergyHistoryTail-i*20),20);
                MeasureBandRepairEnergiesWithTotal(m,m.U32(OffsetAddress(state,DspStateLayout.RepairFilterBanks)),m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairAnalysis)),OffsetAddress(channel,ChannelStateLayout.BandEnergyHistory));
            }
            for(int c=0;c<channels;c++)
            {
                uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride);
                UpdateBandRepairSynthesisFilters(m,channel,OffsetAddress(state,DspStateLayout.SynthesisSpecification),OffsetAddress(state,DspStateLayout.RepairFilterBanks),m.I32(OffsetAddress(state,DspStateLayout.ComparisonMode)),state);
                ApplyCrossfadedBandRepairFilters(m,m.U32(OffsetAddress(state,DspStateLayout.RepairFilterCoefficients)),OffsetAddress(state,DspStateLayout.SynthesisSpecification),m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairAnalysis)),m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput)));
            }
        }
        for(int c=0;c<channels;c++)
        {
            uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride),buffer=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput));
            ProcessRecursiveFilterCascade(m,OffsetAddress(state,DspStateLayout.HighBandAnalysis),buffer,buffer,OffsetAddress(channel,ChannelStateLayout.OutputFilterHistory));
        }
        for(int c=0;c<channels;c++)
        {
            uint channel=OffsetAddress(state,DspStateLayout.Channels+c*DspStateLayout.ChannelStride),buffer=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairInput)),work=m.U32(OffsetAddress(channel,ChannelStateLayout.BandRepairAnalysis));
            int mode=m.I32(OffsetAddress(state,DspStateLayout.SynthesisMode));
            if(mode!=2)
                for(int i=0;i<128;i++)
                {
                    float sample=m.F32(OffsetAddress(channel,ChannelStateLayout.RepairSignalHistory+i*4));
                    m.F32(OffsetAddress(buffer,i*4),mode==1 ? sample : (float)((double)sample+m.F32(OffsetAddress(buffer,i*4))));
                }
            if(output!=0) WriteInterleavedChannel(m,OffsetAddress(buffer,outputOffset*4),output,outputCount,m.I32(OffsetAddress(config,28)),c,channels);
            m.Copy(OffsetAddress(channel,ChannelStateLayout.RepairSignalHistory),OffsetAddress(channel,ChannelStateLayout.RetainedRepairSamples),26*4);
            m.Copy(work,OffsetAddress(work,0x200),40*4);
            m.Copy(OffsetAddress(work,0x2A0),OffsetAddress(work,0x4A0),40*4);
        }
    }

    // 使用共用增益上限和各声道延迟历史应用自适应预加重。
    internal static void ApplyAdaptiveEmphasis(DspMemory m,int channels,int samples,uint controls,uint history,float levelOffset,ReadOnlySpan<uint> inputs,ReadOnlySpan<uint> outputs)
    {
        for(int c=0;c<channels;c++)
        {
            uint delay=OffsetAddress(history,0x1C+c*20);
            for(int i=0;i<samples;i++)
            {
                for(int d=4;d>0;d--) m.U32(OffsetAddress(delay,d*4),m.U32(OffsetAddress(delay,(d-1)*4)));
                float x=m.F32(OffsetAddress(inputs[c],i*4));
                m.F32(delay,x);
                float diff=(float)((double)m.F32(OffsetAddress(delay,8))*0.5-((double)m.F32(OffsetAddress(delay,16))+x)*0.25);
                double alpha=m.F32(OffsetAddress(history,0x74)), beta=m.F32(OffsetAddress(history,0x7C));
                m.F32(OffsetAddress(history,4+c*4),(double)m.F32(OffsetAddress(history,4+c*4))*alpha+(double)diff*diff*(1-alpha));
                double mid=m.F32(OffsetAddress(delay,8));
                m.F32(OffsetAddress(history,12+c*4),beta*m.F32(OffsetAddress(history,12+c*4))+mid*mid*(1-beta));
            }
        }
        m.F32(OffsetAddress(history,0x6C),m.F32(CoefficientTables.InitialEmphasisGain));
        for(int c=0;c<channels;c++)
        {
            float level=(float)Math.Log10(m.F32(OffsetAddress(history,12+c*4)));
            level=(float)((double)level*10); level=(float)((double)level+levelOffset);
            float cap=(float)(-((double)level+m.F32(OffsetAddress(controls,0x50))));
            if(cap<0)cap=0;
            float predicted=(float)((double)level*m.F32(OffsetAddress(controls,0x1C))+m.F32(OffsetAddress(controls,0x20)));
            float detail=(float)Math.Log10(m.F32(OffsetAddress(history,4+c*4)));
            detail=(float)((double)detail*10);detail=(float)((double)detail+levelOffset);
            if(detail<m.F32(OffsetAddress(controls,0x24)))detail=m.F32(OffsetAddress(controls,0x24));
            float gain=(float)((double)predicted-detail);
            if(m.F32(OffsetAddress(controls,0x28))<gain)gain=m.F32(OffsetAddress(controls,0x28));
            if(cap<gain)gain=cap;
            if(gain<m.F32(OffsetAddress(history,0x6C)))m.F32(OffsetAddress(history,0x6C),gain);
        }
        if(m.F32(OffsetAddress(history,0x6C))<0)m.F32(OffsetAddress(history,0x6C),0f);
        m.F32(OffsetAddress(history,0x6C),Math.Pow(10,(double)m.F32(OffsetAddress(history,0x6C))/20));
        for(int c=0;c<channels;c++)
        {
            uint delay=OffsetAddress(history,0x44+c*20);
            for(int i=0;i<samples;i++)
            {
                for(int d=4;d>0;d--)m.U32(OffsetAddress(delay,d*4),m.U32(OffsetAddress(delay,(d-1)*4)));
                float x=m.F32(OffsetAddress(inputs[c],i*4));m.F32(delay,x);
                float pair=(float)(((double)x+m.F32(OffsetAddress(delay,16)))*0.25);
                float mid=(float)((double)m.F32(OffsetAddress(delay,8))*0.5);
                float difference=(float)((double)mid-pair);
                float scaled=(float)((double)difference*m.F32(OffsetAddress(history,0x6C)));
                m.F32(OffsetAddress(outputs[c],i*4),(double)pair+mid+scaled);
            }
        }
        if(m.U32(OffsetAddress(history,0x84))<m.U32(history))
        {m.U32(OffsetAddress(history,0x74),m.U32(OffsetAddress(history,0x70)));m.U32(OffsetAddress(history,0x7C),m.U32(OffsetAddress(history,0x78)));}
        else m.U32(history,unchecked(m.U32(history)+(uint)samples));
    }

    // 在十六个间隔为八的位置测量 41 抽头对称滤波器的能量，并可计入总功率。
    internal static void MeasureBandRepairEnergiesWithTotal(DspMemory m,uint coefficients,uint input,uint energies)=>Stage1Energy(m,coefficients,input,energies,true);
    internal static void MeasureBandRepairEnergies(DspMemory m,uint coefficients,uint input,uint energies)=>Stage1Energy(m,coefficients,input,energies,false);
    private static void Stage1Energy(DspMemory m,uint coefficients,uint input,uint energies,bool total)
    {
        Span<float> accum=stackalloc float[5];accum.Clear();
        Span<float> filtered=stackalloc float[4];
        for(int i=0;i<16;i++)
        {
            uint center=OffsetAddress(input,80+i*32);
            for(int b=0;b<4;b++)filtered[b]=(float)((double)m.F32(center)*m.F32(OffsetAddress(coefficients,b*4)));
            for(int radius=1;radius<=20;radius++)
            {
                float pair=(float)((double)m.F32(OffsetAddress(center,-radius*4))+m.F32(OffsetAddress(center,radius*4)));
                for(int b=0;b<4;b++)filtered[b]=(float)((double)filtered[b]+(double)pair*m.F32(OffsetAddress(coefficients,radius*16+b*4)));
            }
            for(int b=0;b<4;b++)accum[b]=(float)((double)filtered[b]*filtered[b]+accum[b]);
            if(total)
            {
                float sum=(float)((double)filtered[0]+filtered[1]+filtered[2]+filtered[3]);
                accum[4]=(float)((double)sum*sum+accum[4]);
            }
        }
        for(int b=0;b<(total?5:4);b++)m.F32(OffsetAddress(energies,b*4),(double)accum[b]*0.5);
    }

    // 根据交叉淡化表混合两个动态变化的 41 抽头对称滤波器。
    internal static void ApplyCrossfadedBandRepairFilters(DspMemory m,uint coefficients,uint descriptor,uint input,uint output)
    {
        if(TryAdaptiveFirAvx(m,coefficients,descriptor,input,output))return;
        int half=m.I32(OffsetAddress(descriptor,4)),block=half*2;
        if(block<=0)throw new InvalidDataException("Stage-one crossfade block must be positive.");
        for(int start=20;start<148;start+=block)
            for(int j=0;j<block;j++)
            {
                uint center=OffsetAddress(input,(start+j)*4);
                float a=(float)((double)m.F32(center)*m.F32(coefficients));
                float b=(float)((double)m.F32(center)*m.F32(OffsetAddress(coefficients,4)));
                for(int radius=1;radius<=20;radius++)
                {
                    float pair=(float)((double)m.F32(OffsetAddress(center,-radius*4))+m.F32(OffsetAddress(center,radius*4)));
                    a=(float)((double)a+(double)pair*m.F32(OffsetAddress(coefficients,radius*8)));
                    b=(float)((double)b+(double)pair*m.F32(OffsetAddress(coefficients,radius*8+4)));
                }
                uint weights=m.U32(descriptor);
                m.F32(OffsetAddress(output,(start-20+j)*4),(double)m.F32(OffsetAddress(weights,j*4))*a+(double)m.F32(OffsetAddress(weights,(j+block)*4))*b);
            }
    }

    // 根据对数能量预测频带增益，并组合合成滤波器基函数。
    internal static void UpdateBandRepairSynthesisFilters(DspMemory m,uint channel,uint descriptor,uint banks,int comparisonMode,uint state)
    {
        for(int i=0;i<5;i++)Stage1LogEnergy(m,OffsetAddress(channel,ChannelStateLayout.BandEnergyHistory+i*4));
        for(int i=0;i<4;i++)Stage1LogEnergy(m,OffsetAddress(channel,ChannelStateLayout.ComparisonEnergies+i*4));
        Span<float> predictions=stackalloc float[8];
        for(int band=0;band<8;band++)
        {
            if(comparisonMode==0)
            {
                uint row=OffsetAddress(state,DspStateLayout.BandPredictors+band*20);float value=m.F32(row);
                for(int j=0;j<4;j++)value=(float)((double)value+(double)m.F32(OffsetAddress(row,(j+1)*4))*m.F32(OffsetAddress(channel,ChannelStateLayout.BandEnergyHistory+j*4)));
                predictions[band]=value;
            }
            else predictions[band]=(float)((double)m.F32(OffsetAddress(channel,ChannelStateLayout.TotalEnergyHistory))*m.F32(OffsetAddress(state,DspStateLayout.ComparisonPredictorSlopes+band*8))+m.F32(OffsetAddress(state,DspStateLayout.ComparisonPredictors+band*8)));
        }
        int adjustMode=m.I32(OffsetAddress(state,DspStateLayout.PredictionAdjustmentMode));
        double limit=-2.0-m.F32(OffsetAddress(state,DspStateLayout.LevelOffset));
        for(int band=0;band<8;band++)
        {
            if(adjustMode==1)predictions[band]=(float)((double)m.F32(OffsetAddress(state,DspStateLayout.RepairLevelOffset))+m.F32(OffsetAddress(state,DspStateLayout.PredictionBias))+predictions[band]);
            if(limit<predictions[band])predictions[band]=(float)limit;
            if(m.I32(OffsetAddress(state,DspStateLayout.MeasurementFloorEnabled))!=0&&band<4)
            {
                double measured=(double)m.F32(OffsetAddress(channel,ChannelStateLayout.ComparisonEnergies+band*4))+m.F32(OffsetAddress(state,DspStateLayout.MeasurementBias));
                if(predictions[band]<measured)predictions[band]=(float)measured;
            }
            if(adjustMode==0)predictions[band]=(float)((double)m.F32(OffsetAddress(state,DspStateLayout.RepairLevelOffset))+m.F32(OffsetAddress(state,DspStateLayout.PredictionBias))+predictions[band]);
        }
        int bands=m.I32(OffsetAddress(descriptor,12)),split=m.I32(OffsetAddress(descriptor,16));
        for(int i=0;i<bands;i++)
        {
            int sourceBand=m.I32(OffsetAddress(descriptor,20+i*4));
            float gain=(float)Math.Pow(10,((double)predictions[i]-m.F32(OffsetAddress(channel,ChannelStateLayout.BandEnergyHistory+sourceBand*4)))/20);
            if(m.F32(OffsetAddress(state,DspStateLayout.RepairGainLimit))<gain)gain=m.F32(OffsetAddress(state,DspStateLayout.RepairGainLimit));
            m.F32(OffsetAddress(channel,ChannelStateLayout.PredictedGains+i*4),gain);
        }
        for(int i=Math.Max(0,bands);i<8;i++)m.F32(OffsetAddress(channel,ChannelStateLayout.PredictedGains+i*4),0f);
        uint basis=m.U32(banks),destination=m.U32(OffsetAddress(banks,4));
        for(int tap=0;tap<21;tap++)
        {
            float first=0,second=0;
            for(int i=0;i<split;i++)first=(float)((double)first+(double)m.F32(OffsetAddress(channel,ChannelStateLayout.PredictedGains+i*4))*m.F32(OffsetAddress(basis,(tap*4+i)*4)));
            for(int i=split;i<bands;i++)second=(float)((double)second+(double)m.F32(OffsetAddress(channel,ChannelStateLayout.PredictedGains+i*4))*m.F32(OffsetAddress(basis,(tap*4-split+i)*4)));
            m.F32(OffsetAddress(destination,tap*8),first);m.F32(OffsetAddress(destination,tap*8+4),second);
        }
    }

    private static void Stage1LogEnergy(DspMemory m,uint address)
    {
        float value=(float)((double)m.F32(address)*m.F64(CoefficientTables.EnergyNormalization));
        if(value<m.F64(CoefficientTables.PowerThreshold))value=m.F32(CoefficientTables.MinimumEnergy);
        else if(value>m.F32(CoefficientTables.MaximumEnergy))value=m.F32(CoefficientTables.MaximumEnergy);
        float logarithm=(float)Math.Log10(value);
        m.F32(address,(double)logarithm*m.F64(CoefficientTables.LogEnergyScale)-m.F64(CoefficientTables.LogEnergyOffset));
    }
}
