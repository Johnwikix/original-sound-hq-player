using BassPlayerIpc.Shared;
using System.Numerics;

namespace AudioPlayer.Playback;

/// <summary>最多四套曲线系数；文件 IR 不缓存，避免文件覆盖后使用旧内容。</summary>
internal static class PreparedCorrectionCache
{
    internal sealed record Prepared(ConvolutionFilter.Coefficients Coefficients, Complex[][] Spectra, double AutoGainDb);
    private readonly record struct Key(string Points, int Rate, int Channels);
    private static readonly object Gate = new();
    private static readonly Dictionary<Key, Prepared> Entries = new();
    private static readonly Queue<Key> Order = new();

    internal static Prepared Get(DspSettings settings, int rate, int channels, CancellationToken token)
    {
        bool cache = CorrectionCurve.UsesCurve(settings);
        var key = new Key(settings.CurvePoints, rate, channels);
        if (cache) lock (Gate) if (Entries.TryGetValue(key, out var found)) return found;
        token.ThrowIfCancellationRequested();
        var impulse = CorrectionCurve.Prepare(settings, rate, token);
        var prepared = new Prepared(ConvolutionFilter.Prepare(impulse, rate, channels),
            impulse.Channels.Select(ResponseMath.Spectrum).ToArray(), ResponseMath.AutoAttenuationDb(impulse));
        token.ThrowIfCancellationRequested();
        if (cache)
        {
            lock (Gate)
            {
                if (Entries.TryGetValue(key, out var found)) return found;
                if (Entries.Count == 4) Entries.Remove(Order.Dequeue());
                Entries.Add(key, prepared);
                Order.Enqueue(key);
            }
        }
        return prepared;
    }
}
