using AudioPlayer.Decode;

namespace AudioPlayer.Playback;

/// <summary>在解码线程执行流式 WSOLA 时间伸缩；所有声道共用对齐位置。
/// 使用 20 毫秒窗口和有界波形搜索保持音高；一倍速时旁路此处理器。
/// 存储空间有界且与曲目时长无关，定位后继续复用。</summary>
internal sealed class TempoProcessor
{
    private readonly int _channels;
    private readonly int _hopFrames, _searchFrames, _searchStride;
    private readonly double _playbackRate;
    private readonly double[] _input, _tail, _output;
    // 速率不为一时，源帧和输出帧属于不同的时间域。
    private long _inputStartFrame;
    private long _outputFramesProduced;
    private int _inputFrameCount, _outputReadOffset, _outputFrameCount;
    private bool _inputEnded, _hasOverlap;
    private TempoEnvelope _envelope;
    private bool _directMode;
    private int _directOffset;

    internal bool CanDetach => _directMode && _directOffset >= _inputFrameCount && _outputReadOffset == _outputFrameCount;

    internal TempoProcessor(int sampleRate, int channels, double rate, TempoEnvelope envelope = default)
    {
        _channels = channels;
        _playbackRate = rate;
        _envelope = envelope;
        _hopFrames = Math.Max(16, sampleRate / 100);
        _searchFrames = Math.Max(8, sampleRate / 200);
        _searchStride = Math.Max(1, sampleRate / 12000);
        // 保留下一个分析步及其搜索窗口，包括五倍速时较大的源数据跳跃。
        _input = new double[(_hopFrames * ((int)Math.Ceiling(rate) + 4) + _searchFrames * 2 + 16384) * channels];
        _tail = new double[_hopFrames * channels];
        _output = new double[_hopFrames * channels];
    }

    internal void Reset(bool clearEnvelope = false)
    {
        if (clearEnvelope) _envelope = default;
        _directMode = false;
        _directOffset = 0;
        _inputStartFrame = 0;
        _outputFramesProduced = 0;
        _inputFrameCount = 0;
        _outputReadOffset = 0;
        _outputFrameCount = 0;
        _inputEnded = false;
        _hasOverlap = false;
        Array.Clear(_tail);
    }

    internal int Read(PcmDecoder decoder, Span<double> destination)
    {
        int written = 0, requested = destination.Length / _channels;
        while (written < requested)
        {
            if (_directMode && _outputReadOffset == _outputFrameCount)
            {
                int cached = Math.Min(requested - written, _inputFrameCount - _directOffset);
                if (cached > 0)
                {
                    _input.AsSpan(_directOffset * _channels, cached * _channels)
                        .CopyTo(destination[(written * _channels)..]);
                    _directOffset += cached;
                    written += cached;
                    continue;
                }
                int direct = decoder.Read(destination[(written * _channels)..]);
                if (direct == 0) break;
                written += direct;
                continue;
            }
            if (_outputReadOffset == _outputFrameCount && !Generate(decoder)) break;
            int take = Math.Min(requested - written, _outputFrameCount - _outputReadOffset);
            _output.AsSpan(_outputReadOffset * _channels, take * _channels)
                .CopyTo(destination[(written * _channels)..]);
            written += take;
            _outputReadOffset += take;
        }
        return written;
    }

    private bool Generate(PcmDecoder decoder)
    {
        long ideal = (long)Math.Round(_envelope.IsActive
            ? _envelope.SourceFrames(_outputFramesProduced) : _outputFramesProduced * _playbackRate);
        long keep = Math.Max(_inputStartFrame, ideal - _searchFrames);
        int drop = (int)Math.Min(_inputFrameCount, keep - _inputStartFrame);
        if (drop > 0)
        {
            _input.AsSpan(drop * _channels, (_inputFrameCount - drop) * _channels).CopyTo(_input);
            _inputStartFrame += drop;
            _inputFrameCount -= drop;
        }
        long required = ideal + _searchFrames + 2 * _hopFrames;
        while (!_inputEnded && _inputStartFrame + _inputFrameCount < required)
        {
            int read = decoder.Read(_input.AsSpan(_inputFrameCount * _channels));
            if (read == 0) _inputEnded = true;
            else _inputFrameCount += read;
        }
        long remaining = _inputEnded ? (long)Math.Round(_envelope.IsActive
            ? _envelope.OutputFrames(_inputStartFrame + _inputFrameCount)
            : (_inputStartFrame + _inputFrameCount) / _playbackRate) - _outputFramesProduced : _hopFrames;
        if (remaining <= 0 || _inputFrameCount == 0) return false;
        int frames = (int)Math.Min(_hopFrames, remaining);
        int center = (int)Math.Clamp(ideal - _inputStartFrame, 0, Math.Max(0, _inputFrameCount - 2 * _hopFrames));
        int chosen = center;
        bool finishEnvelope = _envelope.IsActive && _outputFramesProduced >= _envelope.EndFrames;
        if (finishEnvelope) chosen = (int)Math.Clamp(ideal - _inputStartFrame, 0, _inputFrameCount);
        if (!finishEnvelope && _hasOverlap && _inputFrameCount >= 2 * _hopFrames)
        {
            int low = Math.Max(0, center - _searchFrames), high = Math.Min(_inputFrameCount - 2 * _hopFrames, center + _searchFrames);
            double best = double.NegativeInfinity;
            // 先粗搜索，再精确到样本细化，限制高采样率输入的搜索开销。
            for (int candidate = low; candidate <= high; candidate += _searchStride)
            {
                double score = Correlation(candidate);
                if (score > best)
                {
                    best = score;
                    chosen = candidate;
                }
            }
            int fineLow = Math.Max(low, chosen - _searchStride), fineHigh = Math.Min(high, chosen + _searchStride);
            for (int candidate = fineLow; candidate <= fineHigh; candidate++)
            {
                double score = Correlation(candidate);
                if (score > best)
                {
                    best = score;
                    chosen = candidate;
                }
            }
        }
        for (int frame = 0; frame < _hopFrames; frame++)
        {
            double mix = (double)frame / _hopFrames;
            for (int ch = 0; ch < _channels; ch++)
            {
                int i = frame * _channels + ch;
                double sample = Sample(chosen + frame, ch);
                _output[i] = _hasOverlap ? _tail[i] * (1 - mix) + sample * mix : sample;
                _tail[i] = Sample(chosen + _hopFrames + frame, ch);
            }
        }
        _hasOverlap = true;
        if (finishEnvelope)
        {
            // One WSOLA overlap blends back to raw PCM. Drain read-ahead before detaching the processor.
            _directMode = true;
            _directOffset = Math.Min(_inputFrameCount, chosen + frames);
        }
        _outputFramesProduced += frames;
        _outputReadOffset = 0;
        _outputFrameCount = frames;
        return true;
    }

    private double Sample(int frame, int channel) => frame < _inputFrameCount ? _input[frame * _channels + channel] : 0;

    private double Correlation(int start)
    {
        double dot = 0, energy = 1e-30;
        for (int frame = 0; frame < _hopFrames; frame += _searchStride)
            for (int ch = 0; ch < _channels; ch++)
            {
                double sample = _input[(start + frame) * _channels + ch];
                dot += _tail[frame * _channels + ch] * sample;
                energy += sample * sample;
            }
        return dot / Math.Sqrt(energy);
    }
}
