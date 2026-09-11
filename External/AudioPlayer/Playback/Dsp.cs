using System.Runtime.CompilerServices;
using System.Threading;
using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>
/// 10 段峰值 EQ（中心频率 32Hz~16kHz，每段独立 Q，范围 0.1~20）。
/// RBJ cookbook 峰值滤波器（Direct Form 1），系数与滤波器状态全程 double
/// （float64）计算与存储：长块级联下累积舍入噪声比 float32 低 ~40dB，
/// 高 Q/低频段（32Hz@44.1kHz，w0→0）系数敏感度最高，double 消除系数量化失真。
/// 系数仅在参数变化时重算并原子换快照（持续激活的带继承滤波器状态，调 EQ 不产生状态跳变），
/// 渲染线程只读快照，无锁无分配。
/// </summary>
internal sealed class Equalizer
{
    public static readonly double[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };

    private struct Band
    {
        public double B0, B1, B2, A1, A2; // a0 已归一化（double 全程）
        public bool Active;
        public int Rate;
    }

    private struct History
    {
        public double X1_0, X2_0, Y1_0, Y2_0;
        public double X1_1, X2_1, Y1_1, Y2_1;
    }

    // 渲染线程读取的不可变快照（引用原子替换）
    private volatile Band[] _snapshot = CreateEmptySnapshot();
    private readonly double[] _gains = new double[10];
    private readonly double[] _q = new double[10];
    private volatile bool _enabled;
    private int _sampleRate;
    private readonly History[] _history = new History[10];
    private Band[]? _renderSnapshot;

    /// <summary>仅由渲染线程清除历史样本，重新启用总开关时避免旧 EQ 尾音泄漏。</summary>
    internal void ResetHistory()
    {
        _history.AsSpan().Clear();
    }

    private static Band[] CreateEmptySnapshot()
    {
        var bands = new Band[10];
        return bands;
    }

    /// <summary>增益来自 IPC 协议（float32，UI 只有一档小数），系数计算在 double 域进行。</summary>
    public void Configure(int sampleRate, bool enabled, ReadOnlySpan<float> gainsDb, ReadOnlySpan<float> qValues = default)
    {
        bool changed = _sampleRate != sampleRate || _enabled != enabled;
        _sampleRate = sampleRate;
        _enabled = enabled;
        for (int i = 0; i < 10; i++)
        {
            double gain = float.IsFinite(gainsDb[i]) ? Math.Clamp(gainsDb[i], -12, 12) : 0;
            double q = EqParameters.NormalizeQ(i < qValues.Length ? qValues[i] : EqParameters.DefaultQ);
            changed |= _gains[i] != gain || _q[i] != q;
            _gains[i] = gain; _q[i] = q;
        }
        if (changed) RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        var rate = _sampleRate;
        var bands = new Band[10];
        for (int i = 0; i < 10; i++)
        {
            ref var b = ref bands[i];
            b.Rate = rate;
            double db = _gains[i];
            b.Active = _enabled && Math.Abs(db) >= 0.01 && rate > 0 && Frequencies[i] < rate * 0.5;
            if (!b.Active) continue;
            // RBJ 峰值滤波器（Q 形式）：Q 越大，峰值作用范围越窄。
            double a = Math.Pow(10.0, db / 40.0);
            double w0 = 2.0 * Math.PI * Frequencies[i] / rate;
            double sinW = Math.Sin(w0), cosW = Math.Cos(w0);
            double alpha = sinW / (2.0 * _q[i]);
            double a0 = 1.0 + alpha / a;
            b.B0 = (1.0 + alpha * a) / a0;
            b.B1 = (-2.0 * cosW) / a0;
            b.B2 = (1.0 - alpha * a) / a0;
            b.A1 = (-2.0 * cosW) / a0;
            b.A2 = (1.0 - alpha / a) / a0;
        }
        _snapshot = bands;
    }

    /// <summary>
    /// 就地处理交织 double 块（≤2 声道，逐声道独立状态）。
    /// &gt;2 声道静默直通：共享模式会话被强制到混音 2ch 不触发；
    /// 仅独占/ASIO 播放多声道文件时出现（与 bass 的 PeakEQ 行为一致）。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(Span<double> interleaved, int frames, int channels)
    {
        var bands = _snapshot;
        if (bands == null || channels is < 1 or > 2) return;
        for (int index = 0; index < bands.Length; index++)
        {
            ref readonly var band = ref bands[index];
            ref var history = ref _history[index];
            if (!ReferenceEquals(bands, _renderSnapshot)
                && (_renderSnapshot == null || !band.Active || !_renderSnapshot[index].Active
                    || band.Rate != _renderSnapshot[index].Rate)) history = default;
            if (!band.Active) continue;
            if (channels == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    double x = interleaved[f];
                    double y = band.B0 * x + band.B1 * history.X1_0 + band.B2 * history.X2_0
                               - band.A1 * history.Y1_0 - band.A2 * history.Y2_0;
                    history.X2_0 = history.X1_0; history.X1_0 = x;
                    history.Y2_0 = history.Y1_0; history.Y1_0 = y;
                    interleaved[f] = y;
                }
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    int i = f * 2;
                    double xl = interleaved[i], xr = interleaved[i + 1];
                    double yl = band.B0 * xl + band.B1 * history.X1_0 + band.B2 * history.X2_0
                                - band.A1 * history.Y1_0 - band.A2 * history.Y2_0;
                    double yr = band.B0 * xr + band.B1 * history.X1_1 + band.B2 * history.X2_1
                                - band.A1 * history.Y1_1 - band.A2 * history.Y2_1;
                    history.X2_0 = history.X1_0; history.X1_0 = xl;
                    history.Y2_0 = history.Y1_0; history.Y1_0 = yl;
                    history.X2_1 = history.X1_1; history.X1_1 = xr;
                    history.Y2_1 = history.Y1_1; history.Y1_1 = yr;
                    interleaved[i] = yl;
                    interleaved[i + 1] = yr;
                }
            }
        }
        _renderSnapshot = bands;
    }
}

/// <summary>控制线程原子发布增益目标；渲染线程独占当前值、步长与剩余帧数。</summary>
internal sealed class GainRamp
{
    private sealed record TargetState(double Gain, int Frames, long ResetVersion, double ResetGain);
    private readonly object _control = new();
    private readonly int _sampleRate;
    private TargetState _target;
    private TargetState? _renderTarget;
    private long _appliedReset;
    private double _current, _step;
    private int _remaining;

    public GainRamp(int sampleRate, double initial = 1)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _current = initial;
        _target = new(initial, 0, 0, initial);
    }

    public double Target => Volatile.Read(ref _target).Gain;

    /// <summary>在下一渲染块安装初值；随后 RampTo 不会丢失尚未消费的初值。</summary>
    public void SetImmediately(double gain)
    {
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 0;
        lock (_control)
        {
            var previous = _target;
            Volatile.Write(ref _target, new(gain, 0, previous.ResetVersion + 1, gain));
        }
    }

    public void RampTo(double gain, int durationMs)
    {
        gain = double.IsFinite(gain) ? Math.Clamp(gain, 0, 1) : 0;
        lock (_control)
        {
            var previous = _target;
            if (previous.Gain == gain && previous.Frames > 0) return;
            int frames = (int)Math.Clamp((long)durationMs * _sampleRate / 1000, 1, int.MaxValue);
            Volatile.Write(ref _target, new(gain, frames, previous.ResetVersion, previous.ResetGain));
        }
    }

    /// <summary>.NET 11 Span 热路径无锁无分配；旧渲染块不回写控制端目标。</summary>
    public void Apply(Span<double> interleaved, int frames, int channels)
    {
        var target = Volatile.Read(ref _target);
        if (!ReferenceEquals(target, _renderTarget))
        {
            if (_appliedReset != target.ResetVersion)
            {
                _current = target.ResetGain;
                _appliedReset = target.ResetVersion;
            }
            _renderTarget = target;
            _remaining = target.Frames;
            _step = _remaining > 0 ? (target.Gain - _current) / _remaining : 0;
            if (_remaining == 0) _current = target.Gain;
        }
        if (_remaining == 0 && _current == 1) return;
        if (_remaining == 0 && _current == 0) { interleaved[..(frames * channels)].Clear(); return; }
        for (int frame = 0; frame < frames; frame++)
        {
            if (_remaining > 0)
            {
                _current += _step;
                if (--_remaining == 0) _current = target.Gain;
            }
            for (int channel = 0; channel < channels; channel++)
                interleaved[frame * channels + channel] *= _current;
        }
    }
}
