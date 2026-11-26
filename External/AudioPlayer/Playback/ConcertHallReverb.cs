using System.Runtime.CompilerServices;

namespace AudioPlayer.Playback;

/// <summary>音乐厅空间混响。四条延迟线通过正交反馈矩阵扩散尾音，反馈低通模拟高频较快消散。</summary>
internal sealed class ConcertHallReverb
{
    private const double DecaySeconds = 3.0;
    private const double MinimumSignal = 1e-20;
    private readonly DelayLine _a, _b, _c, _d;
    private readonly double _damping, _feedLowPass;
    private readonly double _mixStep, _wetRecovery;
    private readonly int _channels, _earlyA, _earlyB, _earlyC, _earlyD;
    private double _mix, _feedMidLow, _feedSideLow;
    private double _wetGain = 1;

    internal ConcertHallReverb(int rate, int channels)
    {
        _channels = channels;
        // 互异的延迟避免反馈重复，与前四个反射之间留出空间。
        _a = new(rate, 0.0413);
        _b = new(rate, 0.0539);
        _c = new(rate, 0.0671);
        _d = new(rate, 0.0797);
        _earlyA = ToFrames(rate, 0.017);
        _earlyB = ToFrames(rate, 0.023);
        _earlyC = ToFrames(rate, 0.031);
        _earlyD = ToFrames(rate, 0.037);
        _damping = 1 - Math.Exp(-2 * Math.PI * 3800 / rate);
        _mixStep = 1.0 / ToFrames(rate, 0.02);
        _wetRecovery = 1 - Math.Exp(-1 / (0.04 * rate));
        _feedLowPass = 1 - Math.Exp(-2 * Math.PI * 130 / rate);
        TailFrames = ToFrames(rate, DecaySeconds + 0.25);
    }

    internal int TailFrames { get; }

    internal void Reset()
    {
        _a.Reset();
        _b.Reset();
        _c.Reset();
        _d.Reset();
        _mix = _feedMidLow = _feedSideLow = 0;
        _wetGain = 1;
    }

    /// <summary>在达到干声时返回 false，允许渲染线程释放关闭的处理器。</summary>
    internal bool Process(Span<double> samples, int frames, bool enabled)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            _mix = enabled ? Math.Min(1, _mix + _mixStep) : Math.Max(0, _mix - _mixStep);
            if (_mix == 0) return enabled;

            int index = frame * _channels;
            double left = FiniteSample(samples[index]);
            double right = _channels == 2 ? FiniteSample(samples[index + 1]) : left;
            // 浮点 PCM 可能超过标称满幅。只限制送入递归延迟的极端值，避免浮点缓冲溢出。
            double feedLeft = Math.Clamp(left, -1e20, 1e20);
            double feedRight = Math.Clamp(right, -1e20, 1e20);
            double middle = (feedLeft + feedRight) * 0.5;
            double side = (feedLeft - feedRight) * 0.25;
            // 只对湿声去除低频，避免延长的反馈在低频堆积。
            _feedMidLow += _feedLowPass * (middle - _feedMidLow);
            _feedSideLow += _feedLowPass * (side - _feedSideLow);
            if (Math.Abs(_feedMidLow) < MinimumSignal) _feedMidLow = 0;
            if (Math.Abs(_feedSideLow) < MinimumSignal) _feedSideLow = 0;
            middle -= _feedMidLow;
            side -= _feedSideLow;

            double earlyLeft = 0.29 * _a.Tap(_earlyA) - 0.17 * _c.Tap(_earlyC);
            double earlyRight = 0.27 * _b.Tap(_earlyB) - 0.16 * _d.Tap(_earlyD);
            double a = _a.ReadDamped(_damping);
            double b = _b.ReadDamped(_damping);
            double c = _c.ReadDamped(_damping);
            double d = _d.ReadDamped(_damping);
            double ab = a + b, aMinusB = a - b, cd = c + d, cMinusD = c - d;
            _a.Write(middle + side, 0.5 * (ab + cd));
            _b.Write(middle - side, 0.5 * (aMinusB + cMinusD));
            _c.Write(-0.7 * middle + side, 0.5 * (ab - cd));
            _d.Write(-0.7 * middle - side, 0.5 * (aMinusB - cMinusD));

            double wetLeft = earlyLeft + 0.29 * (a - d);
            double wetRight = earlyRight + 0.29 * (b - c);
            // 给反射和尾音留余量，并随启停一起平滑干湿声。
            double dry = 1 - 0.18 * _mix;
            double wet = 0.69 * _mix;
            double dryLeft = dry * left;
            if (_channels == 2)
            {
                double dryRight = dry * right;
                wetLeft *= wet;
                wetRight *= wet;
                // 两声道共享湿声增益，避免瞬态削波时改变左右反射的比例。
                RecoverWetGain(Math.Min(WetGainLimit(dryLeft, wetLeft), WetGainLimit(dryRight, wetRight)));
                samples[index] = dryLeft + _wetGain * wetLeft;
                samples[index + 1] = dryRight + _wetGain * wetRight;
            }
            else
            {
                double wetMono = (0.5 * wet) * (wetLeft + wetRight);
                RecoverWetGain(WetGainLimit(dryLeft, wetMono));
                samples[index] = dryLeft + _wetGain * wetMono;
            }
        }

        return enabled || _mix > 0;
    }

    private void RecoverWetGain(double limit)
        => _wetGain = Math.Min(limit, _wetGain + _wetRecovery * (1 - _wetGain));

    private static double WetGainLimit(double dry, double wet)
    {
        // 只限制混响引入的超幅；干声电平由前级与独立的 True Peak 保护决定。
        double peak = Math.Max(1, Math.Abs(dry));
        double sum = dry + wet;
        if (sum > peak) return (peak - dry) / wet;
        if (sum < -peak) return (-peak - dry) / wet;
        return 1;
    }

    private static double FiniteSample(double sample) => double.IsFinite(sample) ? sample : 0;

    private static int ToFrames(int rate, double seconds) => Math.Max(1, (int)Math.Round(rate * seconds));

    private sealed class DelayLine
    {
        private readonly float[] _samples;
        private readonly double _feedback;
        private int _position, _written;
        private double _low;

        internal DelayLine(int rate, double delay)
        {
            _samples = new float[ToFrames(rate, delay)];
            _feedback = Math.Pow(10, -3 * _samples.Length / (rate * DecaySeconds));
        }

        internal void Reset()
        {
            // 读头在缓冲重新写满前只读新样本，避免在渲染线程清整块内存。
            _position = _written = 0;
            _low = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal double Tap(int delay)
        {
            if (_written < delay) return 0;
            int index = _position - delay;
            return _samples[index < 0 ? index + _samples.Length : index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal double ReadDamped(double damping)
        {
            double delayed = _written == _samples.Length ? _samples[_position] : 0;
            _low += damping * (delayed - _low);
            if (Math.Abs(_low) < MinimumSignal) _low = 0;
            return _low;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Write(double input, double feedback)
        {
            _samples[_position] = (float)(input + _feedback * feedback);
            if (++_position == _samples.Length) _position = 0;
            if (_written < _samples.Length) _written++;
        }
    }
}
