namespace AudioPlayer.Playback;

/// <summary>以四倍多相 Hann 窗 sinc 插值估算 True Peak；每声道历史预分配。</summary>
/// <remarks>这是带限插值估计，不是任意 DAC 模拟输出的绝对上界；参考 BS.1770 插值方法。</remarks>
internal sealed class TruePeakMeter
{
    internal const int HistoryFrames = 13;
    private static readonly double[][] Coefficients = CreateCoefficients();
    private readonly double[] _history;
    private readonly int _channels;
    private int _position;
    internal double Peak { get; private set; }

    internal TruePeakMeter(int channels)
    {
        if (channels is < 1 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _history = new double[HistoryFrames * channels];
    }

    internal void Reset()
    {
        Array.Clear(_history);
        _position = 0;
        Peak = 0;
    }

    internal void ContinueFrom(TruePeakMeter previous)
    {
        previous._history.CopyTo(_history, 0);
        _position = previous._position;
        Peak = previous.Peak;
    }

    /// <summary>累计一帧并返回该帧新生成的四相峰值；跨音频块保留历史。</summary>
    internal double AddFrame(ReadOnlySpan<double> frame)
    {
        double peak = 0;
        for (int ch = 0; ch < _channels; ch++)
        {
            double sample = double.IsFinite(frame[ch]) ? frame[ch] : 0;
            _history[_position * _channels + ch] = sample;
            peak = Math.Max(peak, Math.Abs(sample));
            for (int phase = 1; phase < 4; phase++)
            {
                double sum = 0;
                var taps = Coefficients[phase];
                int index = _position;
                for (int tap = 0; tap < HistoryFrames; tap++)
                {
                    sum += taps[tap] * _history[index * _channels + ch];
                    if (--index < 0) index = HistoryFrames - 1;
                }
                peak = Math.Max(peak, double.IsFinite(sum) ? Math.Abs(sum) : double.MaxValue);
            }
        }
        if (++_position == HistoryFrames) _position = 0;
        Peak = Math.Max(Peak, peak);
        return peak;
    }

    internal void Add(ReadOnlySpan<double> samples)
    {
        for (int i = 0; i < samples.Length; i += _channels) AddFrame(samples.Slice(i, _channels));
    }

    internal double Finish()
    {
        Span<double> zero = stackalloc double[8];
        zero.Clear();
        for (int i = 0; i < HistoryFrames; i++) AddFrame(zero[.._channels]);
        return Peak;
    }

    private static double[][] CreateCoefficients()
    {
        var result = new double[4][];
        for (int phase = 0; phase < 4; phase++)
        {
            var taps = result[phase] = new double[HistoryFrames];
            for (int tap = 0; tap < HistoryFrames; tap++)
            {
                int j = tap * 4 + phase;
                if (j > 48) continue;
                double x = (j - 24) / 4.0;
                taps[tap] = (Math.Abs(x) < 1e-12 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x))
                    * 0.5 * (1 - Math.Cos(2 * Math.PI * j / 48));
            }
        }
        return result;
    }
}
