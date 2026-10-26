using BassPlayerIpc.Shared;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace AudioPlayer.Playback;

/// <summary>分级零块延迟卷积：128 点直接段与 128/512/2048 点长尾分区。</summary>
internal sealed class ConvolutionFilter
{
    internal sealed record Coefficients(ConvolutionTier.Coefficients[] Tiers, int Channels, int TailFrames, string Identity);
    internal Coefficients Prepared { get; }
    internal int TailFrames => Prepared.TailFrames;
    private readonly ConvolutionTier[] _tiers;
    private readonly double[] _dry, _sum, _scratch;
    internal bool HasSameCoefficients(ConvolutionFilter other) => ReferenceEquals(Prepared, other.Prepared)
        || Prepared.Identity == other.Prepared.Identity;

    internal ConvolutionFilter(ImpulseResponse impulse, int rate, int channels, bool useSimd = true)
        : this(Prepare(impulse, rate, channels), useSimd) { }

    internal ConvolutionFilter(Coefficients prepared, bool useSimd = true)
    {
        Prepared = prepared;
        _tiers = new ConvolutionTier[prepared.Tiers.Length];
        for (int i = 0; i < _tiers.Length; i++) _tiers[i] = new(prepared.Tiers[i], useSimd);
        _dry = new double[512 * prepared.Channels];
        _sum = new double[_dry.Length]; _scratch = new double[_dry.Length];
    }

    internal static Coefficients Prepare(ImpulseResponse impulse, int rate, int channels, uint outputMask = 0)
    {
        if (channels is < 1 or > 8 || impulse.Channels.Length != 1 && impulse.Channels.Length != channels)
            throw new InvalidDataException("IR must be mono or match output channels exactly.");
        if (impulse.Channels.Length > 1 && impulse.ChannelMask != 0 && outputMask != 0 && impulse.ChannelMask != outputMask)
            throw new InvalidDataException("IR speaker layout does not match output.");
        impulse = impulse.AtRate(rate);
        int count = impulse.Channels[0].Length;
        ImpulseResponse.ValidateBudget(rate, channels, count);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<int> format = stackalloc int[] { rate, channels, count };
        hash.AppendData(MemoryMarshal.AsBytes(format));
        foreach (var channel in impulse.Channels) hash.AppendData(MemoryMarshal.AsBytes(channel.AsSpan()));
        var tiers = new List<ConvolutionTier.Coefficients>();
        for (int block = 128; ; block *= 4)
        {
            int end = block >= 2048 ? count : Math.Min(count, block * 4);
            tiers.Add(ConvolutionTier.Prepare(impulse, rate, channels, block, end, block == 128));
            if (end == count) break;
        }
        return new(tiers.ToArray(), channels, count - 1, Convert.ToHexString(hash.GetHashAndReset()));
    }

    internal void Reset() { foreach (var tier in _tiers) tier.Reset(); }

    internal void Process(Span<double> samples, int frames, double gain, ref double mix, ref double currentGain, double step, bool enabled = true)
    {
        // .NET 11：固定工作区，任意回调大小按 512 帧切片，无托管分配。
        int channels = Prepared.Channels;
        for (int offset = 0; offset < frames; offset += 512)
        {
            int take = Math.Min(512, frames - offset), length = take * channels;
            var block = samples.Slice(offset * channels, length);
            for (int i = 0; i < length; i++) _dry[i] = double.IsFinite(block[i]) ? block[i] : 0;
            Array.Clear(_sum, 0, length);
            foreach (var tier in _tiers)
            {
                _dry.AsSpan(0, length).CopyTo(_scratch);
                double fullMix = 1, unityGain = 1;
                tier.Process(_scratch, take, 1, ref fullMix, ref unityGain, 1);
                for (int i = 0; i < length; i++) _sum[i] += _scratch[i];
            }
            for (int frame = 0; frame < take; frame++)
            {
                mix = enabled ? Math.Min(1, mix + step) : Math.Max(0, mix - step);
                currentGain += Math.Clamp(gain - currentGain, -step, step);
                for (int ch = 0; ch < channels; ch++)
                {
                    int i = frame * channels + ch;
                    double output = _sum[i] * currentGain;
                    block[i] = double.IsFinite(output) ? _dry[i] + (output - _dry[i]) * mix : 0;
                }
            }
        }
    }
}
