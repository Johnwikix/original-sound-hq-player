namespace BassPlayerIpc.Shared;

/// <summary>Identifies the PCM sources eligible for spectral enhancement.</summary>
public enum SpectralEnhancementMode : byte
{
    /// <summary>Disables enhancement.</summary>
    Off,
    /// <summary>Enhances supported lossy PCM sources.</summary>
    LossyOnly,
    /// <summary>Also enhances lossless PCM sources sampled at no more than 48 kHz.</summary>
    LossyAndLowRateLossless
}

/// <summary>Resolves enhancement eligibility and the PCM rate presented to an output device.</summary>
public static class SpectralEnhancementPolicy
{
    /// <summary>Gets whether a decoded source is eligible before downmixing or rate conversion.</summary>
    public static bool IsEligible(SpectralEnhancementMode mode, int sourceRate, int sourceChannels,
        bool lossless, bool dsd, bool spatial) => mode is SpectralEnhancementMode.LossyOnly or SpectralEnhancementMode.LossyAndLowRateLossless
        && !dsd && !spatial && sourceChannels is 1 or 2 && sourceRate is > 0 and <= 48000
        && (!lossless || mode == SpectralEnhancementMode.LossyAndLowRateLossless);

    /// <summary>Gets twice the source rate for exclusive/ASIO, or a device-mix divisor at no more than 192 kHz for shared output.</summary>
    public static int OutputRate(int sourceRate, int? deviceRate)
    {
        if (deviceRate is not > 0) return checked(sourceRate * 2);
        int divisor = Math.Max(1, (deviceRate.Value + 191999) / 192000);
        while (deviceRate.Value % divisor != 0) divisor++;
        return deviceRate.Value / divisor;
    }
}
