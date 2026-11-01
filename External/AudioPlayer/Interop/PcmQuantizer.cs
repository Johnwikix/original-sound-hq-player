using System.Runtime.CompilerServices;

namespace AudioPlayer.Interop;

/// <summary>量化归一化的有符号 PCM；输出实例独占可选 TPDF 抖动状态。</summary>
internal struct PcmQuantizer
{
    private uint _random;

    /// <summary>按 2^(bits-1) 缩放、就近偶数舍入并饱和，保留无处理整数 PCM 往返。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static int Quantize(double sample, int bits)
    {
        if (!double.IsFinite(sample)) return 0;
        double scale = Math.ScaleB(1, bits - 1);
        return (int)Math.Clamp(Math.Round(sample * scale, MidpointRounding.ToEven), -scale, scale - 1);
    }

    /// <summary>可选添加两个独立均匀分布之差；静音保持精确零，不分配缓冲。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal int Convert(double sample, int bits, bool dither)
    {
        if (dither && double.IsFinite(sample) && sample != 0)
            sample += (Next() - Next()) * Math.ScaleB(1, 1 - bits);
        return Quantize(sample, bits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private double Next()
    {
        uint state = _random == 0 ? 0xA341316Cu : _random;
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        _random = state;
        return state * (1.0 / 4294967296.0);
    }
}
