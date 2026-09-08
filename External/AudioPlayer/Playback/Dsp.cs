using System.Runtime.CompilerServices;

namespace AudioPlayer.Playback;

/// <summary>
/// 10 段峰值 EQ（与 bass_fx PeakEQ 同参：中心频率 32Hz~16kHz，带宽 1.0 倍频程）。
/// RBJ cookbook 峰值滤波器（Direct Form 1），系数仅在参数变化时重算并原子换快照，
/// 渲染线程只读快照，无锁无分配。
/// </summary>
internal sealed class Equalizer
{
    public static readonly float[] Frequencies = { 32, 64, 125, 250, 500, 1000, 2000, 4000, 8000, 16000 };
    private const float BandwidthOctaves = 1.0f;

    private struct Band
    {
        public float B0, B1, B2, A1, A2; // a0 已归一化
        public float X1_0, X2_0, Y1_0, Y2_0; // ch0 状态
        public float X1_1, X2_1, Y1_1, Y2_1; // ch1 状态
        public bool Active;
    }

    // 渲染线程读取的不可变快照（引用原子替换）
    private volatile Band[] _snapshot = CreateEmptySnapshot();
    private readonly float[] _gains = new float[10];
    private volatile bool _enabled;
    private int _sampleRate;

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
            float db = _gains[i];
            b.Active = _enabled && MathF.Abs(db) >= 0.01f && rate > 0;
            if (!b.Active) continue;
            // RBJ 峰值滤波器（带宽形式）
            float a = MathF.Pow(10f, db / 40f);
            float w0 = 2f * MathF.PI * Frequencies[i] / rate;
            float sinW = MathF.Sin(w0), cosW = MathF.Cos(w0);
            float alpha = sinW * MathF.Sinh(MathF.Log(2f) * 0.5f * BandwidthOctaves * w0 / MathF.Max(sinW, 1e-9f));
            float a0 = 1f + alpha / a;
            b.B0 = (1f + alpha * a) / a0;
            b.B1 = (-2f * cosW) / a0;
            b.B2 = (1f - alpha * a) / a0;
            b.A1 = (-2f * cosW) / a0;
            b.A2 = (1f - alpha / a) / a0;
        }
        _snapshot = bands;
    }

    /// <summary>就地处理交织 float 块（≤2 声道，逐声道独立状态）。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Process(Span<float> interleaved, int frames, int channels)
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
                    float x = interleaved[f];
                    float y = band.B0 * x + band.B1 * band.X1_0 + band.B2 * band.X2_0
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
                    float xl = interleaved[i], xr = interleaved[i + 1];
                    float yl = band.B0 * xl + band.B1 * band.X1_0 + band.B2 * band.X2_0
                               - band.A1 * band.Y1_0 - band.A2 * band.Y2_0;
                    float yr = band.B0 * xr + band.B1 * band.X1_1 + band.B2 * band.X2_1
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
/// 渲染线程每样本推进；目标/斜率的更新只写字段（宽松一致性即可，误差一帧块）。
/// </summary>
internal sealed class GainRamp
{
    private float _current = 1f;
    private volatile float _target = 1f;
    private volatile float _perSampleStep = 0f;
    private readonly int _sampleRate;

    public GainRamp(int sampleRate, float initial = 1f)
    {
        _sampleRate = Math.Max(1, sampleRate);
        _current = initial;
        _target = initial;
    }

    public float Target => _target;

    /// <summary>立即生效（用于会话起始，避免新会话从旧增益斜坡起步）。</summary>
    public void SetImmediately(float gain)
    {
        _target = gain;
        _perSampleStep = 0f;
        _current = gain;
    }

    /// <summary>在 durationMs 内线性过渡到目标增益。</summary>
    public void RampTo(float gain, int durationMs)
    {
        _target = gain;
        int frames = Math.Max(1, (int)((long)durationMs * _sampleRate / 1000));
        _perSampleStep = (gain - _current) / frames;
        if (MathF.Abs(_perSampleStep) < 1e-9f) _perSampleStep = 0f;
    }

    /// <summary>就地应用（交织，全部声道同增益）。gain==1 且无斜坡时零成本通过。</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Apply(Span<float> interleaved, int frames, int channels)
    {
        float step = _perSampleStep;
        if (step == 0f && _current == _target)
        {
            if (_current == 1f) return;
            float gain = _current;
            if (gain == 0f) { interleaved[..(frames * channels)].Clear(); return; }
            Multiply(interleaved, frames * channels, gain);
            return;
        }
        float g = _current;
        float target = _target;
        // 斜坡按【帧】推进：交织流逐样本推进会让左右声道错位、且立体声下时长减半
        for (int f = 0; f < frames; f++)
        {
            g += step;
            if ((step > 0f && g > target) || (step < 0f && g < target)) { g = target; _perSampleStep = 0f; }
            int b = f * channels;
            for (int c = 0; c < channels; c++) interleaved[b + c] *= g;
        }
        _current = g;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Multiply(Span<float> span, int count, float gain)
    {
        for (int i = 0; i < count; i++) span[i] *= gain;
    }
}
