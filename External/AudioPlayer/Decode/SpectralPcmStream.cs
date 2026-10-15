using AudioPlayer.AudioEnhancement;
using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

/// <summary>Pulls enhanced PCM on the decoder thread, trims DSP delay and resamples to the negotiated device rate.</summary>
/// <remarks>Buffers belong to one decoder. Reads reuse storage; seeking resets signal history before new samples enter the ring.</remarks>
internal sealed unsafe class SpectralPcmStream : IDisposable
{
    private readonly SpectralEnhancer _core;
    private readonly AllPassProcessor _allPass;
    private readonly int _channels, _ratio, _latency;
    private readonly double[] _decoded, _converted;
    private readonly int[] _input, _middle, _output = new int[1024];
    private readonly double[] _enhanced;
    private SwrContext* _post;
    private int _pendingOffset, _pendingFrames, _skip;
    private long _inputFrames, _emitted;
    private bool _inputEnded, _ended;

    internal SpectralPcmStream(int inputRate, int outputRate, int channels, bool lossless, int bitrate, int bits)
    {
        _channels = channels;
        _ratio = outputRate >= inputRate * 4 ? 4 : 2;
        _core = new(new() { InputSampleRate = inputRate, OutputSampleRate = inputRate * _ratio,
            SourceFormatSelector = lossless ? 16 : 4, AverageBitRateKbps = Math.Clamp(bitrate / 1000, 4, 1020) });
        _allPass = new(inputRate, lossless ? 0 : 3, Math.Max(4000, bitrate), bits);
        _latency = _core.LatencyFrames + (inputRate == 44100 ? 5 : 6) * _ratio;
        _skip = _latency;
        _input = new int[_core.InputFramesPerBlock * 2];
        _middle = new int[_input.Length];
        _decoded = new double[_core.InputFramesPerBlock * channels];
        _enhanced = new double[512 * channels];
        _converted = new double[(512 * Math.Max(1, (outputRate + inputRate * _ratio - 1) / (inputRate * _ratio)) + 256) * channels];
        AVChannelLayout layout = default;
        ffmpeg.av_channel_layout_default(&layout, channels);
        SwrContext* post = null;
        try
        {
            int result = ffmpeg.swr_alloc_set_opts2(&post, &layout, AVSampleFormat.AV_SAMPLE_FMT_DBL, outputRate,
                &layout, AVSampleFormat.AV_SAMPLE_FMT_DBL, inputRate * _ratio, 0, null);
            if (result < 0 || post == null || ffmpeg.swr_init(post) < 0)
                throw new IOException("Cannot initialize spectral enhancement output converter.");
            _post = post;
        }
        catch { if (post != null) ffmpeg.swr_free(&post); throw; }
        finally { ffmpeg.av_channel_layout_uninit(&layout); }
    }

    internal void Reset()
    {
        _core.Reset();
        _allPass.Reset();
        _pendingOffset = _pendingFrames = 0;
        _inputFrames = _emitted = 0;
        _inputEnded = _ended = false;
        _skip = _latency;
        ffmpeg.swr_close(_post);
        if (ffmpeg.swr_init(_post) < 0) throw new IOException("Cannot reset enhancement output converter.");
    }

    internal int Read(PcmDecoder decoder, Span<double> destination)
    {
        // .NET 10+：所有工作缓冲只在打开时分配，Span 不跨线程或异步边界。
        int written = 0, requested = destination.Length / _channels;
        while (written < requested)
        {
            if (_pendingOffset < _pendingFrames)
            {
                int take = Math.Min(requested - written, _pendingFrames - _pendingOffset);
                _converted.AsSpan(_pendingOffset * _channels, take * _channels).CopyTo(destination[(written * _channels)..]);
                written += take;
                _pendingOffset += take;
                continue;
            }
            if (_ended) break;
            Generate(decoder);
        }
        return written;
    }

    private void Generate(PcmDecoder decoder)
    {
        _pendingOffset = _pendingFrames = 0;
        int filled = 0;
        while (!_inputEnded && filled < _core.InputFramesPerBlock)
        {
            int read = decoder.ReadDecoded(_decoded.AsSpan(0, (_core.InputFramesPerBlock - filled) * _channels));
            if (read <= 0) { _inputEnded = true; break; }
            for (int frame = 0; frame < read; frame++)
            {
                int left = ToInteger(_decoded[frame * _channels]);
                _input[(filled + frame) * 2] = left;
                _input[(filled + frame) * 2 + 1] = _channels == 1 ? left : ToInteger(_decoded[frame * 2 + 1]);
            }
            filled += read;
            _inputFrames += read;
        }
        if (_inputEnded && _emitted == _inputFrames * _ratio)
        {
            Convert(0);
            _ended = _pendingFrames == 0;
            return;
        }
        _input.AsSpan(filled * 2).Clear();
        _allPass.ProcessBlock(_input, _middle);
        _core.ProcessBlock(_middle, _output);
        int offset = Math.Min(512, _skip);
        _skip -= offset;
        int frames = (int)Math.Min(512 - offset, _inputFrames * _ratio - _emitted);
        for (int frame = 0; frame < frames; frame++)
            for (int channel = 0; channel < _channels; channel++)
                _enhanced[frame * _channels + channel] = _output[(offset + frame) * 2 + channel] / 2147483648.0;
        _emitted += frames;
        if (frames > 0) Convert(frames);
    }

    private void Convert(int frames)
    {
        fixed (double* output = _converted, input = _enhanced)
        {
            double* source = input;
            double* target = output;
            int got = ffmpeg.swr_convert(_post, (byte**)&target, _converted.Length / _channels, frames == 0 ? null : (byte**)&source, frames);
            if (got < 0) throw new IOException($"Enhancement output conversion failed: {got}.");
            _pendingFrames = got;
        }
    }

    private static int ToInteger(double sample) => !double.IsFinite(sample) ? 0
        : (int)Math.Clamp(Math.Round(sample * 2147483648.0), int.MinValue, int.MaxValue);

    /// <summary>Releases the resampler owned by this decoder-thread processor.</summary>
    public void Dispose()
    {
        if (_post != null) { SwrContext* post = _post; _post = null; ffmpeg.swr_free(&post); }
    }
}
