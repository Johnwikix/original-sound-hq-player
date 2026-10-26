using BassPlayerIpc.Shared;
using System.Numerics;

namespace AudioPlayer.Playback;

/// <summary>最多四套曲线系数，按最近使用顺序淘汰；文件 IR 不缓存。</summary>
internal static class PreparedCorrectionCache
{
    /// <summary>发布后禁止修改所有层级的数组；渲染历史必须由滤波器单独持有。</summary>
    internal sealed record Prepared(ConvolutionFilter.Coefficients Coefficients, Complex[][] Spectra, double AutoGainDb);
    private readonly record struct Key(string Points, int Rate, int Channels);
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Prepared Value)>> Entries = new();
    private static readonly LinkedList<(Key Key, Prepared Value)> Order = new();

    // 命中时复用节点，更新 LRU 顺序不产生分配；调用者持有 Gate。
    private static Prepared Touch(LinkedListNode<(Key Key, Prepared Value)> node)
    {
        Order.Remove(node);
        Order.AddLast(node);
        return node.Value.Value;
    }

    internal static Prepared Get(DspSettings settings, int rate, int channels, CancellationToken token, uint channelMask = 0)
    {
        bool cache = CorrectionCurve.UsesCurve(settings);
        var key = new Key(settings.CurvePoints, rate, channels);
        if (cache) lock (Gate) if (Entries.TryGetValue(key, out var found)) return Touch(found);
        token.ThrowIfCancellationRequested();
        var impulse = CorrectionCurve.Prepare(settings, rate, token);
        var coefficients = ConvolutionFilter.Prepare(impulse, rate, channels, channelMask);
        var spectra = new Complex[impulse.Channels.Length][];
        double peak = 1;
        for (int ch = 0; ch < spectra.Length; ch++)
        {
            token.ThrowIfCancellationRequested();
            spectra[ch] = ResponseMath.Spectrum(impulse.Channels[ch]);
            for (int i = 0; i <= spectra[ch].Length / 2; i++) peak = Math.Max(peak, spectra[ch][i].Magnitude);
        }
        var prepared = new Prepared(coefficients, spectra, -20 * Math.Log10(peak));
        token.ThrowIfCancellationRequested();
        if (cache)
        {
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var found)) return Touch(found);
                if (Entries.Count == 4)
                {
                    Entries.Remove(Order.First!.Value.Key);
                    Order.RemoveFirst();
                }
                Entries.Add(key, Order.AddLast((key, prepared)));
            }
        }
        return prepared;
    }
}
