using System;
using BassPlayerIpc.Shared;

namespace WinUIMusicPlayer.Services;

[Flags]
public enum LicenseFeature
{
    None = 0,
    Convolution = 1 << 0,
    LoudnessNormalization = 1 << 1,
    Preamp = 1 << 2,
    Balance = 1 << 3,
    ChannelSwap = 1 << 4,
    Mono = 1 << 5,
    Crossfeed = 1 << 6,
    StereoWidth = 1 << 7,
    DsdBitstream = 1 << 8,
    Surround51 = 1 << 9,
    AtmosPassthrough = 1 << 10,
    AllDsp = Convolution | LoudnessNormalization | Preamp | Balance | ChannelSwap | Mono | Crossfeed | StereoWidth,
    AllOutput = DsdBitstream | Surround51 | AtmosPassthrough
}

/// <summary>产品门控清单。UI、偏好写入和播放端快照共用；调整已有功能的收费范围只修改此清单。</summary>
public static class LicensePolicy
{
    public const LicenseFeature RestrictedFeatures = LicenseFeature.Convolution
        | LicenseFeature.DsdBitstream | LicenseFeature.Surround51 | LicenseFeature.AtmosPassthrough;

    public static bool Contains(LicenseFeature restricted, LicenseFeature feature) => (restricted & feature) != 0;

    /// <summary>只改变发往引擎的副本；许可恢复时重新发送原偏好。</summary>
    public static DspSettings ApplyDspRestrictions(DspSettings settings, LicenseFeature restricted)
    {
        if ((restricted & LicenseFeature.AllDsp) == 0) return settings;
        return settings with
        {
            ConvolutionEnabled = settings.ConvolutionEnabled && !Contains(restricted, LicenseFeature.Convolution),
            NormalizeLoudness = settings.NormalizeLoudness && !Contains(restricted, LicenseFeature.LoudnessNormalization),
            AutoPreamp = Contains(restricted, LicenseFeature.Preamp) ? false : settings.AutoPreamp,
            HeadroomDb = Contains(restricted, LicenseFeature.Preamp) ? 0 : settings.HeadroomDb,
            Balance = Contains(restricted, LicenseFeature.Balance) ? 0 : settings.Balance,
            SwapChannels = settings.SwapChannels && !Contains(restricted, LicenseFeature.ChannelSwap),
            Mono = settings.Mono && !Contains(restricted, LicenseFeature.Mono),
            Crossfeed = Contains(restricted, LicenseFeature.Crossfeed) ? 0 : settings.Crossfeed,
            StereoWidth = Contains(restricted, LicenseFeature.StereoWidth) ? 1 : settings.StereoWidth
        };
    }

    /// <summary>批量编辑/重置只写可用功能，锁定功能的历史偏好保持不变。</summary>
    public static DspSettings PreserveRestrictedPreferences(DspSettings candidate, DspSettings saved, LicenseFeature restricted)
    {
        if ((restricted & LicenseFeature.AllDsp) == 0) return candidate;
        bool convolution = Contains(restricted, LicenseFeature.Convolution);
        bool loudness = Contains(restricted, LicenseFeature.LoudnessNormalization);
        bool preamp = Contains(restricted, LicenseFeature.Preamp);
        return candidate with
        {
            ConvolutionEnabled = convolution ? saved.ConvolutionEnabled : candidate.ConvolutionEnabled,
            ConvolutionSource = convolution ? saved.ConvolutionSource : candidate.ConvolutionSource,
            ImpulsePath = convolution ? saved.ImpulsePath : candidate.ImpulsePath,
            CurvePoints = convolution ? saved.CurvePoints : candidate.CurvePoints,
            CurvePresetName = convolution ? saved.CurvePresetName : candidate.CurvePresetName,
            ConvolutionTrimDb = convolution ? saved.ConvolutionTrimDb : candidate.ConvolutionTrimDb,
            AutoConvolutionHeadroom = convolution ? saved.AutoConvolutionHeadroom : candidate.AutoConvolutionHeadroom,
            NormalizeLoudness = loudness ? saved.NormalizeLoudness : candidate.NormalizeLoudness,
            TargetLufs = loudness ? saved.TargetLufs : candidate.TargetLufs,
            AutoPreamp = preamp ? saved.AutoPreamp : candidate.AutoPreamp,
            HeadroomDb = preamp ? saved.HeadroomDb : candidate.HeadroomDb,
            Balance = Contains(restricted, LicenseFeature.Balance) ? saved.Balance : candidate.Balance,
            SwapChannels = Contains(restricted, LicenseFeature.ChannelSwap) ? saved.SwapChannels : candidate.SwapChannels,
            Mono = Contains(restricted, LicenseFeature.Mono) ? saved.Mono : candidate.Mono,
            Crossfeed = Contains(restricted, LicenseFeature.Crossfeed) ? saved.Crossfeed : candidate.Crossfeed,
            StereoWidth = Contains(restricted, LicenseFeature.StereoWidth) ? saved.StereoWidth : candidate.StereoWidth
        };
    }

    public static void ApplyOutputRestrictions(ref IpcSetting settings, LicenseFeature restricted)
    {
        if (Contains(restricted, LicenseFeature.DsdBitstream)) settings.IsDopEnabled = false;
        if (Contains(restricted, LicenseFeature.Surround51)) settings.ExperimentalSurround51 = false;
        if (Contains(restricted, LicenseFeature.AtmosPassthrough)) settings.ExperimentalAtmosPassthrough = false;
    }
}
