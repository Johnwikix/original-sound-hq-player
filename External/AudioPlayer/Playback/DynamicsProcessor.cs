using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>联动峰值压缩器，使用 6 dB 软拐点、补偿增益和 -1 dBFS 样本峰值上限。
/// 渲染线程持有历史状态，控制线程发布不可变设置。</summary>
internal sealed class DynamicsProcessor(int sampleRate, int channels)
{
    private readonly double _attack = Math.Exp(-1.0 / (sampleRate * 0.005));
    private readonly double _release = Math.Exp(-1.0 / (sampleRate * 0.150));
    private const double Ceiling = 0.8912509381337456;
    private double _reductionDb, _limiterGain = 1;
    private bool _active;

    internal void ContinueFrom(DynamicsProcessor previous)
    {
        _reductionDb = previous._reductionDb;
        _limiterGain = previous._limiterGain;
        _active = previous._active;
    }

    internal void Reset()
    {
        _reductionDb = 0;
        _limiterGain = 1;
        _active = false;
    }

    internal void Process(Span<double> samples, int frames, DspSettings settings)
    {
        if (!settings.IsEnabled || !settings.CompressorEnabled)
        {
            if (_active) Reset();
            return;
        }
        _active = true;
        double slope = 1 - 1 / settings.CompressorRatio;
        for (int frame = 0; frame < frames; frame++)
        {
            int offset = frame * channels;
            double peak = 0;
            for (int ch = 0; ch < channels; ch++)
            {
                ref double sample = ref samples[offset + ch];
                if (!double.IsFinite(sample)) sample = 0;
                peak = Math.Max(peak, Math.Abs(sample));
            }
            double over = 20 * Math.Log10(Math.Max(1e-15, peak)) - settings.CompressorThresholdDb;
            double reduction = over <= -3 ? 0 : over >= 3 ? slope * over : slope * (over + 3) * (over + 3) / 12;
            double coefficient = reduction > _reductionDb ? _attack : _release;
            _reductionDb = coefficient * _reductionDb + (1 - coefficient) * reduction;
            double gain = Math.Pow(10, (settings.CompressorMakeupDb - _reductionDb) / 20);
            // 所有声道使用相同增益，保持立体声或环绕声声像。
            // 限幅器立即响应，捕捉压缩器起控期间的瞬态峰值。
            double limit = peak > 0 ? Math.Min(1, Ceiling / peak / gain) : 1;
            _limiterGain = limit < _limiterGain ? limit : _release * _limiterGain + (1 - _release) * limit;
            gain *= _limiterGain;
            for (int ch = 0; ch < channels; ch++) samples[offset + ch] *= gain;
        }
    }
}
