namespace AudioPlayer.AudioEnhancement;

/// <summary>指定双声道 PCM 处理的 DSP 设置。</summary>
/// <remarks>音源格式使用编号选择，DSP 参数明确标注单位。</remarks>
public sealed record SpectralEnhancementOptions
{
    /// <summary>获取输入采样率，目前支持 44100 或 48000 Hz。</summary>
    public int InputSampleRate { get; init; } = 44100;
    /// <summary>获取同一采样率系列中一倍、二倍或四倍的目标输出采样率。</summary>
    public int OutputSampleRate { get; init; } = 88200;
    /// <summary>获取处理模式：零为旁路，一为频带修复，二为升频，三为同时允许两个阶段。</summary>
    public int Mode { get; init; } = 3;
    /// <summary>获取系数表使用的音源格式选择编号。</summary>
    public int SourceFormatSelector { get; init; } = 4;
    /// <summary>获取原始音源采样率；为零时使用输入采样率。</summary>
    public int OriginalSampleRate { get; init; }
    /// <summary>获取音源是否使用可变码率。</summary>
    public bool VariableBitRate { get; init; }
    /// <summary>获取音源平均码率，单位为千比特每秒；DSP 配置将其量化为四的倍数。</summary>
    public int AverageBitRateKbps { get; init; } = 192;
    /// <summary>获取指定的频带边界索引；为零时查表选择。</summary>
    public int BandBoundaryOverride { get; init; }
    /// <summary>获取余量参数，单位为十分之一分贝。</summary>
    public int HeadMarginTenthsDb { get; init; } = 12;
    /// <summary>获取 DCR 标志。</summary>
    public bool DcrEnabled { get; init; }
    /// <summary>获取增益补偿参数，单位为十分之一。</summary>
    public int GainCompensationTenths { get; init; } = 1;
    /// <summary>获取增强偏移，单位为十分之一分贝。</summary>
    public int EnhancementOffsetTenthsDb { get; init; } = 3;

    internal int[] ToConfigurationFields() => [Mode,InputSampleRate,OutputSampleRate,SourceFormatSelector,
        OriginalSampleRate==0?InputSampleRate:OriginalSampleRate,VariableBitRate?1:0,AverageBitRateKbps,
        BandBoundaryOverride,HeadMarginTenthsDb,DcrEnabled?1:0,GainCompensationTenths,EnhancementOffsetTenthsDb];
}
