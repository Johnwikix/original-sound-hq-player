namespace AudioPlayer.Playback;

/// <summary>计算单/双声道 BS.1770 K 加权综合响度和源采样峰值，完全不修改样本。</summary>
internal sealed class LoudnessMeter
{
    private readonly Biquad[] _shelf;
    private readonly Biquad[] _highPass;
    private readonly double[] _energyWindow;
    private readonly List<double> _blocks = new();
    private readonly int _channels, _hop;
    private int _index, _untilBlock;
    private long _frames;
    private double _energy, _peak;
    private bool _invalid;

    internal LoudnessMeter(int sampleRate, int channels)
    {
        if (sampleRate < 8000 || channels is < 1 or > 2) throw new ArgumentOutOfRangeException(nameof(channels));
        _channels = channels;
        _hop = (int)Math.Round(sampleRate * 0.1);
        _energyWindow = new double[_hop * 4];
        _untilBlock = _energyWindow.Length;
        _shelf = new Biquad[channels];
        _highPass = new Biquad[channels];
        // BS.1770 的两个 K 加权级；按实际采样率重算双线性变换系数。
        double k = Math.Tan(Math.PI * 1681.974450955533 / sampleRate);
        const double q = 0.7071752369554196;
        double high = Math.Pow(10, 3.999843853973347 / 20);
        double band = Math.Pow(high, 0.4996667741545416);
        double denominator = 1 + k / q + k * k;
        var shelf = new Biquad((high + band * k / q + k * k) / denominator,
            2 * (k * k - high) / denominator, (high - band * k / q + k * k) / denominator,
            2 * (k * k - 1) / denominator, (1 - k / q + k * k) / denominator);
        k = Math.Tan(Math.PI * 38.13547087602444 / sampleRate);
        denominator = 1 + k / 0.5003270373238773 + k * k;
        var highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / denominator,
            (1 - k / 0.5003270373238773 + k * k) / denominator);
        Array.Fill(_shelf, shelf);
        Array.Fill(_highPass, highPass);
    }

    /// <summary>以 Span 累计分析，窗口内不分配；块统计仅由后台扫描线程维护。</summary>
    internal void Add(ReadOnlySpan<double> samples)
    {
        for (int offset = 0; offset < samples.Length; offset += _channels)
        {
            double energy = 0;
            for (int ch = 0; ch < _channels; ch++)
            {
                double sample = samples[offset + ch];
                if (!double.IsFinite(sample)) { _invalid = true; sample = 0; }
                _peak = Math.Max(_peak, Math.Abs(sample));
                double filtered = _highPass[ch].Process(_shelf[ch].Process(sample));
                energy += filtered * filtered;
            }
            _energy += energy - _energyWindow[_index];
            _energyWindow[_index] = energy;
            if (++_index == _energyWindow.Length) _index = 0;
            _frames++;
            if (--_untilBlock == 0)
            {
                _blocks.Add(Math.Max(0, _energy / _energyWindow.Length));
                _untilBlock = _hop;
            }
        }
    }

    /// <summary>应用 -70 LUFS 绝对门限及 -10 LU 相对门限；短于 400ms 或静音返回空。</summary>
    internal LoudnessMeasurement? Finish()
    {
        if (_invalid || _frames < _energyWindow.Length || _peak <= 0) return null;
        double absolute = Math.Pow(10, (-70 + 0.691) / 10);
        double sum = 0;
        int count = 0;
        foreach (double energy in _blocks)
            if (energy >= absolute) { sum += energy; count++; }
        if (count == 0) return null;
        double threshold = Math.Max(absolute, sum / count * 0.1);
        sum = 0;
        count = 0;
        foreach (double energy in _blocks)
            if (energy >= threshold) { sum += energy; count++; }
        double lufs = -0.691 + 10 * Math.Log10(sum / count);
        return double.IsFinite(lufs) ? new(lufs, _peak) : null;
    }

    private struct Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _x1, _x2, _y1, _y2;
        internal double Process(double sample)
        {
            double y = b0 * sample + b1 * _x1 + b2 * _x2 - a1 * _y1 - a2 * _y2;
            _x2 = _x1; _x1 = sample; _y2 = _y1; _y1 = y;
            return y;
        }
    }
}

/// <summary>保存整曲测量值，以固定衰减替代动态压缩来保留峰值余量。</summary>
internal sealed record LoudnessMeasurement(double IntegratedLufs, double SamplePeak)
{
    internal double GainDb(double targetLufs) => Math.Min(
        Math.Clamp(targetLufs - IntegratedLufs, -60, 24), -1 - 20 * Math.Log10(SamplePeak));
}
