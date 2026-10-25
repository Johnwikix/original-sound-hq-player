namespace AudioPlayer.AudioEnhancement;

/// <summary>对整数 PCM 应用立体声全通延迟和随音源变化的增益。</summary>
/// <remarks>根据音源模式编号选择对应的增益系数。</remarks>
public sealed partial class AllPassProcessor
{
    // 使用两个镜像的 1589 样本延迟环和两个镜像的 21 样本延迟环。
    private readonly float[] longDelay = new float[0x18D4];
    private readonly float[] shortDelay = new float[84];
    private readonly int[] lengths;
    private int longPosition,shortPosition;
    private float gain;
    private static readonly uint[][] GainBits =
    [
        // 每组音源模式保存十三个按码率选择的增益系数。
        [0x3D0702DA,0x3D047CF7,0x3D04233C,0x3D01EBAC,0x3D01EBAC,0x3D01EBAC,0x3D03C613,0x3D01D011,0x3D021CBA,0x3D01D334,0x3D0120F7,0x3D00D655,0x3D00D655],
        [0x3D07800C,0x3D0313BA,0x3D0266F4,0x3D026C31,0x3D01F5C9,0x3D01F5C9,0x3D023FE7,0x3D0232B9,0x3D025153,0x3D01F5B6,0x3D00CB55,0x3D00CB55,0x3D00CB55],
        [0x3D03E8FE,0x3D02959B,0x3D02959B,0x3D0201EE,0x3D0201EE,0x3D019809,0x3D0198B3,0x3D00FF99,0x3D00ED25,0x3D00ED25,0x3CFF9F96,0x3CFF9F96,0x3CFEC0CC]
    ];

    /// <summary>初始化 <see cref="AllPassProcessor"/> 类的新实例。</summary>
    /// <param name="sampleRate">支持的处理采样率。</param>
    /// <param name="sourceMode">零到十之间的音源模式编号。</param>
    /// <param name="bitrate">音源码率，单位为比特每秒。</param>
    /// <param name="sourceBitDepth">音源模式零和一使用的源位深。</param>
    /// <exception cref="ArgumentOutOfRangeException">不支持指定的采样率或音源配置。</exception>
    public AllPassProcessor(int sampleRate,int sourceMode=3,int bitrate=128000,int sourceBitDepth=16)
    {
        SampleRate=sampleRate;
        lengths=sampleRate switch
        {
            32000=>[67,41,29,59,43,23,4],
            44100=>[89,53,41,83,61,37,5],
            48000=>[97,61,43,89,67,41,6],
            88200=>[179,113,79,163,127,73,9],
            96000=>[193,127,89,179,131,83,10],
            176400=>[359,223,157,331,241,149,18],
            192000=>[389,241,173,353,269,163,20],
            _=>throw new ArgumentOutOfRangeException(nameof(sampleRate))
        };
        SetSource(sourceMode,bitrate,sourceBitDepth);
    }

    /// <summary>获取处理采样率。</summary>
    public int SampleRate { get; }
    /// <summary>获取当前音源对应的处理增益。</summary>
    public float Gain=>gain;

    /// <summary>更新音源对应的增益，并保留延迟历史。</summary>
    /// <param name="sourceMode">零到十之间的音源模式编号。</param>
    /// <param name="bitrate">以比特每秒为单位的码率；模式二到十要求该值为正数。</param>
    /// <param name="sourceBitDepth">音源模式零和一使用的位深，可选 8、16、20、24 或 32。</param>
    /// <exception cref="ArgumentOutOfRangeException">不支持指定的音源参数；校验失败时增益归零。</exception>
    public void SetSource(int sourceMode,int bitrate,int sourceBitDepth=16)
    {
        gain=0;
        if(sourceMode is <0 or >10 || (sourceMode>=2&&bitrate<=0))throw new ArgumentOutOfRangeException(nameof(sourceMode));
        uint bits;
        if(sourceMode<=1)
            bits=sourceBitDepth switch {8=>0x3B01F0C8,16=>0x3701F0C8,20=>0x34DCE65A,24=>0x32E5F68D,32=>0,_=>throw new ArgumentOutOfRangeException(nameof(sourceBitDepth))};
        else if(sourceMode<=4)
        {
            ReadOnlySpan<int> edges=[64000,80000,96000,112000,128000,132000,160000,192000,224000,256000,288000,320000];
            int bin=0;while(bin<edges.Length&&bitrate>edges[bin])bin++;
            bits=GainBits[sourceMode-2][bin];
        }
        else bits=sourceMode switch {5=>0x3D958BD4,6=>0x3D958C29,7=>0x3D91FA39,8=>0x3D89A8F8,9=>0x3D00ED25,10=>0x3D8D957F,_=>0};
        gain=BitConverter.UInt32BitsToSingle(bits);
    }

    /// <summary>重置两个声道的延迟历史，保留采样率和音源增益。</summary>
    public void Reset(){Array.Clear(longDelay);Array.Clear(shortDelay);longPosition=0;shortPosition=0;}

    /// <summary>处理一个交织排列的立体声有符号 32 位 PCM 数据块。</summary>
    /// <param name="input">包含一个或多个完整帧的交织立体声输入。</param>
    /// <param name="output">目标区域，允许与输入区域完全重叠。</param>
    public void ProcessBlock(ReadOnlySpan<int> input,Span<int> output)=>Process(input,output);

    /// <summary>使用持续保存的延迟状态处理交织立体声有符号 32 位 PCM。</summary>
    /// <param name="input">交织立体声输入，必须包含正整数个完整帧。</param>
    /// <param name="output">目标区域，允许与输入区域完全重叠。</param>
    /// <exception cref="ArgumentException">输入为空、包含不完整帧、超出目标容量或与目标部分重叠。</exception>
    public void Process(ReadOnlySpan<int> input,Span<int> output)
    {
        if(input.Length==0||(input.Length&1)!=0||output.Length<input.Length)throw new ArgumentException("Provide complete stereo frames and a large enough destination.");
        if(input.Overlaps(output,out int displacement)&&displacement!=0)throw new ArgumentException("Only exact in-place overlap is supported.");
        // 系数保持双精度，每次写入 float 时对中间结果舍入。
        const double a=0.6484,b=0.6016,c=0.5391;
        for(int frame=0;frame<input.Length/2;frame++)
        {
            for(int channel=0;channel<2;channel++)
            {
                int x=input[frame*2+channel],basis=channel*3178,shortBasis=channel*42;
                int i1=longPosition+lengths[0],i2=i1+lengths[1],i3=i2+lengths[2];
                int i4=i3+lengths[3],i5=i4+lengths[4],i6=i5+lengths[5];
                shortDelay[shortBasis+shortPosition]=x;
                shortDelay[shortBasis+shortPosition+21]=x;
                float d1=longDelay[basis+i1];
                float v1=FlushTiny((float)(x-d1*a));WriteLong(basis,longPosition,v1);
                float r1=(float)(v1*a+d1);
                float d2=longDelay[basis+i2];
                float v2=FlushTiny((float)(r1-d2*b));WriteLong(basis,i1,v2);
                float r2=(float)(v2*b+d2);
                float d3=longDelay[basis+i3];
                float v3=FlushTiny((float)(r2-d3*c));WriteLong(basis,i2,v3);
                float first=(float)(v3*c+d3);
                float d4=longDelay[basis+i4];
                float v4=FlushTiny((float)(d4*a+x));WriteLong(basis,i3,v4);
                float r4=(float)(d4-v4*a);
                float d5=longDelay[basis+i5];
                float v5=FlushTiny((float)(d5*b+r4));WriteLong(basis,i4,v5);
                float r5=(float)(d5-v5*b);
                float d6=longDelay[basis+i6];
                float v6=FlushTiny((float)(d6*c+r5));WriteLong(basis,i5,v6);
                float r6=(float)(d6-v6*c);
                float sample=(float)(((double)r6+first)*gain+shortDelay[shortBasis+shortPosition+lengths[6]]);
                output[frame*2+channel]=sample>2147483647.0?int.MaxValue:sample<-2147483648.0?int.MinValue:unchecked((int)(long)sample);
            }
            longPosition=longPosition==0?1588:longPosition-1;
            shortPosition=shortPosition==0?20:shortPosition-1;
        }
    }

    private static float FlushTiny(float value)=>value<0.0009765625f&&value> -0.0009765625f?0:value;
    private void WriteLong(int basis,int index,float value)
    {longDelay[basis+index]=value;longDelay[basis+(index<1589?index+1589:index-1589)]=value;}

}
