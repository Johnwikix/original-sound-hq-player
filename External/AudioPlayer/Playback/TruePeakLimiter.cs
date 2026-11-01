namespace AudioPlayer.Playback;

/// <summary>使用四倍峰值检测与 5 ms 前瞻的声道联动保护器，位于最终 PCM 增益之后。</summary>
internal sealed class TruePeakLimiter
{
    private readonly int _channels, _delay, _attackFrames;
    private readonly double[] _audio, _peaks;
    private readonly long[] _indices;
    private readonly TruePeakMeter _meter;
    private readonly double _release;
    private int _position, _head, _tail;
    private long _frame;
    private double _gain = 1, _attackTarget = 1, _attackStep;
    private int _attackRemaining;
    internal int DelayFrames => _delay;

    internal TruePeakLimiter(int rate, int channels)
    {
        _channels = channels;
        _delay = Math.Max(TruePeakMeter.HistoryFrames * 2, (int)Math.Ceiling(rate * 0.005));
        _attackFrames = Math.Max(1, Math.Min(_delay - TruePeakMeter.HistoryFrames, (int)Math.Ceiling(rate * 0.001)));
        _audio = new double[(_delay + 1) * channels];
        _peaks = new double[_delay + TruePeakMeter.HistoryFrames * 2 + 3];
        _indices = new long[_peaks.Length];
        _meter = new(channels);
        _release = Math.Exp(-1.0 / (rate * 0.15));
    }

    internal void Reset()
    {
        Array.Clear(_audio);
        _meter.Reset();
        _position = _head = _tail = 0;
        _frame = 0;
        _gain = _attackTarget = 1; _attackRemaining = 0; _attackStep = 0;
    }

    internal void ContinueFrom(TruePeakLimiter previous)
    {
        previous._audio.CopyTo(_audio, 0);
        previous._peaks.CopyTo(_peaks, 0);
        previous._indices.CopyTo(_indices, 0);
        _meter.ContinueFrom(previous._meter);
        _position = previous._position; _head = previous._head; _tail = previous._tail;
        _frame = previous._frame; _gain = previous._gain;
        _attackTarget = previous._attackTarget; _attackStep = previous._attackStep; _attackRemaining = previous._attackRemaining;
    }

    /// <summary>原位处理任意帧块，单调队列维护有界前瞻峰值；不分配、不锁定。</summary>
    internal void Process(Span<double> samples, int frames, double ceilingDb)
    {
        double ceiling = Math.Pow(10, (ceilingDb - 0.15) / 20); // 插值及增益变化预留少量余量。
        int capacity = _peaks.Length;
        for (int frame = 0; frame < frames; frame++)
        {
            var input = samples.Slice(frame * _channels, _channels);
            double peak = _meter.AddFrame(input);
            while (_head != _tail && _indices[_head] < _frame - _delay - TruePeakMeter.HistoryFrames)
                _head = (_head + 1) % capacity;
            while (_head != _tail)
            {
                int last = (_tail + capacity - 1) % capacity;
                if (_peaks[last] > peak) break;
                _tail = last;
            }
            _peaks[_tail] = peak; _indices[_tail] = _frame;
            _tail = (_tail + 1) % capacity;
            double target = _peaks[_head] > ceiling ? ceiling / _peaks[_head] : 1;
            // 前瞻内用 1 ms 线性下降，避免硬切增益自行制造插值过冲。
            // 检测器历史和前瞻之间保留充分余量，使目标在对应音频到达前生效。
            if (target < _gain && target < _attackTarget)
            {
                _attackTarget = target; _attackRemaining = _attackFrames;
                _attackStep = (_gain - target) / _attackFrames;
            }
            if (_attackRemaining > 0)
            {
                _gain = Math.Max(_attackTarget, _gain - _attackStep);
                if (--_attackRemaining == 0) _gain = _attackTarget;
            }
            else
            {
                _gain = _release * _gain + (1 - _release) * target;
                _attackTarget = _gain;
            }
            int read = (_position + 1) % (_delay + 1);
            for (int ch = 0; ch < _channels; ch++)
            {
                double sample = double.IsFinite(input[ch]) ? input[ch] : 0;
                double delayed = _audio[read * _channels + ch];
                _audio[_position * _channels + ch] = sample;
                input[ch] = delayed * _gain;
            }
            _position = read;
            _frame++;
        }
    }
}
