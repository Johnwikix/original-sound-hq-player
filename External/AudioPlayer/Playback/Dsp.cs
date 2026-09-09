using System.Runtime.CompilerServices;
using System.Threading;

namespace AudioPlayer.Playback;

/// <summary>
/// 10 段峰值 EQ（与 bass_fx PeakEQ 同参：中心频率 32Hz~16kHz，带宽 1.0 倍频程）。
/// RBJ cookbook 峰值滤波器（Direct Form 1），系数与滤波器状态全程 double
/// （float64）计算与存储：长块级联下累积舍入噪声比 float32 低 ~40dB，
/// 高 Q/低频段（32Hz@44.1kHz，w0→0）系数敏感度最高，double 消除系数量化失真。
/// 系数仅在参数变化时重算并原子换快照（持续激活的带继承滤波器状态，调 EQ 不产生状态跳变），
/// 渲染线程只读快照，无锁无分配。
/// </summary>
internal sealed class Equalizer
{
    public static readonly double[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private const double BandwidthOctaves = 1.0;

    private struct Band
    {
        public double B0, B1, B2, A1, A2; // a0 已归一化（double 全程）
        public double X1_0, X2_0, Y1_0, Y2_0; // ch0 状态
        public double X1_1, X2_1, Y1_1, Y2_1; // ch1 状态
        public bool Active;
    }

    // 渲染线程读取的不可变快照（引用原子替换）
    private volatile Band[] _snapshot = CreateEmptySnapshot();
    private readonly double[] _gains = new double[10];
    private volatile bool _enabled;
    private int _sampleRate;
    private int _snapshotRate; // 上次快照的采样率：一致才继承滤波器状态

    public bool Enabled => _enabled;
    public bool Active => _enabled && HasActiveBand(_snapshot);

    private static Band[] CreateEmptySnapshot()
    {
        var bands = new Band[10];
        return bands;
    }

    private static bool HasActiveBand(Band[] bands)
    {
        foreach (var b in bands) if (b.Active) return true;
        return false;
    }

    /// <summary>增益来自 IPC 协议（float32，UI 只有一档小数），系数计算在 double 域进行。</summary>
    public void Configure(int sampleRate, bool enabled, ReadOnlySpan<float> gainsDb)
    {
        _sampleRate = sampleRate;
        _enabled = enabled;
        for (int i = 0; i < 10; i++) _gains[i] = gainsDb[i];
        RebuildSnapshot();
    }

    public void SetEnabled(bool enabled)
    {
        _enabled = enabled;
        RebuildSnapshot();
    }

    public void UpdateGains(ReadOnlySpan<float> gainsDb)
    {
        for (int i = 0; i <10; i++) _gains[i] = gainsDb[i];
        RebuildSnapshot();
    }

    private void RebuildSnapshot()
    {
        var rate = _sampleRate;
        var old = _snapshot;
        var bands = new Band[10];
        for (int i = 0; i < 10; i++)
        {
            ref var b = ref bands[i];
            double db = _gains[i];
            b.Active = _enabled && Math.Abs(db) >= 0.01 && rate > 0;
            if (!b.Active) continue;
            // 状态连续性：带持续激活且采样率未变时继承旧快照的滤波器状态，
            // 系数热更新不产生状态跳变（播放中调 EQ 的爆音）；新激活/换率从零起步
            if (old[i].Active && rate == _snapshotRate)
            {
                b.X1_0 = old[i].X1_0; b.X2_0 = old[i].X2_0;
                b.Y1_0 = old[i].Y1_0; b.Y2_0 = old[i].Y2_0;
                b.X1_1 = old[i].X1_1; b.X2_1 = old[i].X2_1;
                b.Y1_1 = old[i].Y1_1; b.Y2_1 = old[i].Y2_1;
            }
            // RBJ 峰值滤波器（带宽形式），全程 double
            double a = Math.Pow(10.0, db / 40.0);
            double w0 = 2.0 * Math.PI * Frequencies[i] / rate;
            double sinW = Math.Sin(w0), cosW = Math.Cos(w0);
            double alpha = sinW * Math.Sinh(Math.Log(2.0) * 0.5 * BandwidthOctaves * w0 / Math.Max(sinW, 1e-9));
            double a0 = 1.0 + alpha / a;
            b.B0 = (1.0 + alpha * a) / a0;
            b.B1 = (-2.0 * cosW) / a0;
            b.B2 = (1.0 - alpha * a) / a0;
            b.A1 = (-2.0 * cosW) / a0;
            b.A2 = (1.0 - alpha / a) / a0;
        }
        _snapshotRate = rate;
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
        foreach (ref var band in bands.AsSpan()) // struct 数组可 ref 遍历
        {
            if (!band.Active) continue;
            if (channels == 1)
            {
                for (int f = 0; f < frames; f++)
                {
                    double x = interleaved[f];
                    double y = band.B0 * x + band.B1 * band.X1_0 + band.B2 * band.X2_0
                               - band.A1 * band.Y1_0 - band.A2 * band.Y2_0;
                    band.X2_0 = band.X1_0; band.X1_0 = x;
                    band.Y2_0 = band.Y1_0; band.Y1_0 = y;
                    interleaved[f] = y;
                }
            }
            else
            {
                for (int f = 0; f < frames; f++)
                {
                    int i = f * 2;
                    double xl = interleaved[i], xr = interleaved[i + 1];
                    double yl = band.B0 * xl + band.B1 * band.X1_0 + band.B2 * band.X2_0
                                - band.A1 * band.Y1_0 - band.A2 * band.Y2_0;
                    double yr = band.B0 * xr + band.B1 * band.X1_1 + band.B2 * band.X2_1
                                - band.A1 * band.Y1_1 - band.A2 * band.Y2_1;
                    band.X2_0 = band.X1_0; band.X1_0 = xl;
                    band.Y2_0 = band.Y1_0; band.Y1_0 = yl;
                    band.X2_1 = band.X1_1; band.X1_1 = xr;
                    band.Y2_1 = band.Y1_1; band.Y1_1 = yr;
                    interleaved[i] = yl;
                    interleaved[i + 1] = yr;
                }
            }
        }
    }
}

/// <summary>
/// 采样精确的线性增益斜坡：音量变化走短斜坡（防 zipper），淡入淡出走长斜坡。
/// 增益与斜率全程 double。渲染线程每样本推进；目标/斜率的更新只写字段
/// （宽松一致性即可，误差一帧块）；double/long 不可 volatile 修饰，
/// 用 Volatile.Read/Write 保证跨线程可见性。
/// </summary>
internal sealed class GainRamp
{
    private double _current = 1.0;
    private double _target = 1.0;
    private double _perSampleStep = 0.0;
    private readonly int _sampleRate;

    public GainRamp(int sampleRate, double initial = 1.0)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _current = initial;
        _target = initial;
    }

    public double Target => Volatile.Read(ref _target);

    /// <summary>立即生效（用于会话起始，避免新会话从旧增益斜坡起步）。</summary>
    public void SetImmediately(double gain)
    {
        Volatile.Write(ref _target, gain);
        Volatile.Write(ref _perSampleStep, 0.0);
        _current = gain;
    }

    /// <summary>在 durationMs 内线性过渡到目标增益。</summary>
    public void RampTo(double gain, int durationMs)
    {
        Volatile.Write(ref _target, gain);
        int frames = Math.Max(1, (int)((long)durationMs * _sampleRate / 1000));
        double step = (gain - _current) / frames;
        if (Math.Abs(step) < 1e-12) step = 0.0;
        Volatile.Write(ref _perSampleStep, step);
    }

    /// <summary>就地应用（交织，全部声道同增益）。gain==1 且无斜坡时零成本通过。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(Span<double> interleaved, int frames, int channels)
    {
        double step = Volatile.Read(ref _perSampleStep);
        double target = Volatile.Read(ref _target);
        if (step == 0.0 && _current == target)
        {
            if (_current == 1.0) return;
            double gain = _current;
            if (gain == 0.0) { interleaved[..(frames * channels)].Clear(); return; }
            Multiply(interleaved, frames * channels, gain);
            return;
        }
        double g = _current;
        // 斜坡按【帧】推进：交织流逐样本推进会让左右声道错位、且立体声下时长减半
        for (int f = 0; f < frames; f++)
        {
            g += step;
            if ((step > 0.0 && g > target) || (step < 0.0 && g < target)) { g = target; Volatile.Write(ref _perSampleStep, 0.0); }
            int b = f * channels;
            for (int c = 0; c < channels; c++) interleaved[b + c] *= g;
        }
        _current = g;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Multiply(Span<double> span, int count, double gain)
    {
        for (int i = 0; i < count; i++) span[i] *= gain;
    }
}
