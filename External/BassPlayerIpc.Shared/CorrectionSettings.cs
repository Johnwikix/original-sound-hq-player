namespace BassPlayerIpc.Shared;

/// <summary>设备专属校正数据；全局增益、均衡及声道设置不属于设备绑定。</summary>
public sealed record CorrectionSettings
{
    public ConvolutionSource ConvolutionSource { get; init; }
    public string CurvePoints { get; init; } = CorrectionCurve.Flat;
    public string CurvePresetName { get; init; } = "";
    public string ImpulsePath { get; init; } = "";
    public double ConvolutionTrimDb { get; init; } = -6;
    public bool AutoConvolutionHeadroom { get; init; } = true;

    public static implicit operator CorrectionSettings(DspSettings value) => new()
    {
        ConvolutionSource = value.ConvolutionSource, CurvePoints = value.CurvePoints,
        CurvePresetName = value.CurvePresetName, ImpulsePath = value.ImpulsePath,
        ConvolutionTrimDb = value.ConvolutionTrimDb, AutoConvolutionHeadroom = value.AutoConvolutionHeadroom
    };

    public static implicit operator DspSettings(CorrectionSettings value) => new()
    {
        ConvolutionSource = value.ConvolutionSource, CurvePoints = value.CurvePoints,
        CurvePresetName = value.CurvePresetName, ImpulsePath = value.ImpulsePath,
        ConvolutionTrimDb = value.ConvolutionTrimDb, AutoConvolutionHeadroom = value.AutoConvolutionHeadroom
    };

    public CorrectionSettings ToUnifiedGain() => (CorrectionSettings)((DspSettings)this).Sanitize();
}
