namespace BassPlayerIpc.Shared;

/// <summary>选择普通 PCM 的输出速率；DSD 及已有兼容回退不使用此策略。</summary>
public enum PcmResampleMode : byte
{
    /// <summary>保持源采样率。</summary>
    Source,
    /// <summary>使用指定目标采样率。</summary>
    Fixed,
    /// <summary>选择不超过目标的源采样率整数倍，目标低于源率时保持源率。</summary>
    IntegerMultiple
}

/// <summary>校验并计算受支持的 PCM 输出速率，不打开设备。</summary>
public static class PcmResampling
{
    /// <summary>获取输出目标，保持现有非标准源率时不作隐式变换。</summary>
    public static int Resolve(PcmResampleMode mode, int sourceRate, int targetRate)
    {
        if (sourceRate <= 0) throw new ArgumentOutOfRangeException(nameof(sourceRate));
        return mode switch
        {
            PcmResampleMode.Fixed => targetRate,
            PcmResampleMode.IntegerMultiple => Math.Max(1, targetRate / sourceRate) * sourceRate,
            _ => sourceRate
        };
    }

    /// <summary>返回受支持的配置目标，非法值恢复 192 kHz。</summary>
    public static int ValidateTarget(int rate) => rate is 44100 or 48000 or 88200 or 96000 or 176400 or 192000
        or 352800 or 384000 or 705600 or 768000 ? rate : 192000;
}
