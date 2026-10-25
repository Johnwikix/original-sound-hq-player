using System.Runtime.InteropServices;

namespace AudioPlayer.AudioEnhancement;

/// <summary>使用托管频谱增强内核处理立体声整数 PCM。</summary>
/// <remarks>使用嵌入系数，并在数据块之间持续保存信号历史。</remarks>
public sealed class SpectralEnhancer
{
    private DspMemory memory;
    private readonly int[] fields;

    /// <summary>初始化 <see cref="SpectralEnhancer"/> 类的新实例。</summary>
    /// <param name="configuration">输入、输出和增强设置。</param>
    /// <exception cref="ArgumentNullException">配置为空。</exception>
    /// <exception cref="NotSupportedException">处理器不支持指定的采样率系列。</exception>
    public SpectralEnhancer(SpectralEnhancementOptions configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);Configuration=configuration;
        fields=configuration.ToConfigurationFields();memory=CreateMemory(fields);
    }

    /// <summary>获取创建此处理器时使用的配置。</summary>
    public SpectralEnhancementOptions Configuration { get; }
    /// <summary>获取每个数据块消耗的立体声输入帧数。</summary>
    public int InputFramesPerBlock => memory.I32(CoreDsp.OffsetAddress(DspMemory.StateAddress,DspStateLayout.InputFrames));
    /// <summary>获取每个数据块生成的立体声输出帧数。</summary>
    public int OutputFramesPerBlock => 512;
    /// <summary>获取选定的频带边界索引。</summary>
    public int BandBoundaryIndex => memory.I32(CoreDsp.OffsetAddress(DspMemory.StateAddress,DspStateLayout.BandBoundary));
    /// <summary>获取频带修复是否已启用。</summary>
    public bool BandRepairActive => memory.I32(CoreDsp.OffsetAddress(DspMemory.StateAddress,DspStateLayout.BandRepairEnabled))!=0;
    /// <summary>获取升频是否已启用。</summary>
    public bool RateExtensionActive => memory.I32(CoreDsp.OffsetAddress(DspMemory.StateAddress,DspStateLayout.RateExtensionEnabled))!=0;

    /// <summary>重置所有信号历史、预测器历史和伪随机状态。</summary>
    public void Reset() => memory=CreateMemory(fields);

    private static DspMemory CreateMemory(ReadOnlySpan<int> configuration)
    {
        var storage = new DspMemory();
        CoreDsp.Initialize(storage,DspMemory.StateAddress,configuration);
        return storage;
    }

    /// <summary>读取一个立体声交织有符号 32 位 PCM 数据块，并写入一个输出块。</summary>
    /// <param name="input">数量恰为 <see cref="InputFramesPerBlock"/> 两倍的整数样本。</param>
    /// <param name="output">至少可容纳 1024 个交织整数样本的空间。</param>
    /// <exception cref="ArgumentException">输入或输出区域长度不正确。</exception>
    public void ProcessBlock(ReadOnlySpan<int> input,Span<int> output)
    {
        if(input.Length!=InputFramesPerBlock*2)throw new ArgumentException("Incorrect input block size.",nameof(input));
        if(output.Length<1024)throw new ArgumentException("The output requires 1024 samples.",nameof(output));
        if(BitConverter.IsLittleEndian)MemoryMarshal.AsBytes(input).CopyTo(memory.Write(DspMemory.InputAddress,input.Length*4));
        else for(int i=0;i<input.Length;i++)memory.I32(CoreDsp.OffsetAddress(DspMemory.InputAddress,i*4),input[i]);
        memory.Clear(DspMemory.OutputAddress,4096);
        CoreDsp.ProcessIntegerBlock(memory,DspMemory.StateAddress,DspMemory.InputAddress,DspMemory.OutputAddress);
        if(BitConverter.IsLittleEndian)memory.Read(DspMemory.OutputAddress,4096).CopyTo(MemoryMarshal.AsBytes(output[..1024]));
        else for(int i=0;i<1024;i++)output[i]=memory.I32(CoreDsp.OffsetAddress(DspMemory.OutputAddress,i*4));
    }

    /// <summary>获取配置的滤波器延迟，以输出帧数计。</summary>
    public int LatencyFrames => memory.I32(CoreDsp.OffsetAddress(DspMemory.StateAddress,DspStateLayout.Latency));

    internal DspMemory Memory => memory;
}
