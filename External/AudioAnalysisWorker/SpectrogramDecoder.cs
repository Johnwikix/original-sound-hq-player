using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using WinUIMusicPlayer.AudioAnalysis;

namespace WinUIMusicPlayer.AudioAnalysis;

/// <summary>独立于 MIR 的顺序解码路径；保持原采样率，单声道保持单声道，多声道由 FFmpeg 下混为立体声。</summary>
internal sealed unsafe class SpectrogramDecoder : IDisposable
{
    private sealed class Deadline { public long Ticks; }
    private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
        Stopwatch.GetTimestamp() >= ((Deadline)GCHandle.FromIntPtr((nint)opaque).Target!).Ticks ? 1 : 0;
    private readonly Deadline _deadline = new();
    private GCHandle _handle;
    private AVFormatContext* _format;
    private AVCodecContext* _decoder;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private int _stream;
    private bool _disposed;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public double Duration { get; private set; }

    public SpectrogramDecoder(string path)
    {
        _handle = GCHandle.Alloc(_deadline);
        AVDictionary* options = null;
        AVChannelLayout layout = default;
        try
        {
            RefreshDeadline();
            _format = ffmpeg.avformat_alloc_context();
            if (_format == null) throw new OutOfMemoryException();
            _format->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = (void*)GCHandle.ToIntPtr(_handle) };
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            fixed (AVFormatContext** format = &_format) Check(ffmpeg.avformat_open_input(format, path, null, &options));
            Check(ffmpeg.avformat_find_stream_info(_format, null));
            _stream = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Check(_stream);
            var stream = _format->streams[_stream];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec == null) throw new InvalidDataException("Decoder unavailable.");
            _decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (_decoder == null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_to_context(_decoder, stream->codecpar));
            _decoder->thread_count = 1;
            Check(ffmpeg.avcodec_open2(_decoder, codec, null));
            SampleRate = _decoder->sample_rate;
            Channels = _decoder->ch_layout.nb_channels;
            if (SampleRate is < 1000 or > 768000 || Channels is < 1 or > 32) throw new InvalidDataException("Unsupported audio layout.");
            if (_decoder->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                ffmpeg.av_channel_layout_uninit(&_decoder->ch_layout);
                ffmpeg.av_channel_layout_default(&_decoder->ch_layout, Channels);
            }
            ffmpeg.av_channel_layout_default(&layout, Math.Min(Channels, 2));
            fixed (SwrContext** resampler = &_resampler)
                Check(ffmpeg.swr_alloc_set_opts2(resampler, &layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
                    &_decoder->ch_layout, _decoder->sample_fmt, SampleRate, 0, null));
            Check(ffmpeg.swr_init(_resampler));
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            if (_packet == null || _frame == null) throw new OutOfMemoryException();
            Duration = stream->duration > 0 ? stream->duration * ffmpeg.av_q2d(stream->time_base)
                : _format->duration / (double)ffmpeg.AV_TIME_BASE;
            if (!double.IsFinite(Duration) || Duration <= 0 || Duration > 86400) throw new InvalidDataException("Unknown audio duration.");
        }
        catch { Dispose(); throw; }
        finally { ffmpeg.av_dict_free(&options); ffmpeg.av_channel_layout_uninit(&layout); }
    }

    public SpectrogramData Analyze(int fftSize, Action<double> progress)
    {
        var accumulator = new SpectrogramAccumulator(SampleRate, Math.Min(Channels, 2), Duration, fftSize);
        var buffer = new float[131072];
        int channels = Math.Min(Channels, 2), lastProgress = -1;
        int invalidPackets = 0;
        bool draining = false;
        while (true)
        {
            RefreshDeadline();
            int receive = ffmpeg.avcodec_receive_frame(_decoder, _frame);
            if (receive == 0)
            {
                invalidPackets = 0;
                int capacity = ffmpeg.swr_get_out_samples(_resampler, _frame->nb_samples);
                Check(capacity);
                int needed = checked(capacity * channels);
                if (needed > 2097152) throw new InvalidDataException("Audio frame too large.");
                if (needed > buffer.Length) Array.Resize(ref buffer, needed);
                int count;
                fixed (float* output = buffer)
                {
                    byte* target = (byte*)output;
                    count = ffmpeg.swr_convert(_resampler, &target, buffer.Length / channels, _frame->extended_data, _frame->nb_samples);
                }
                Check(count);
                accumulator.Add(buffer.AsSpan(0, count * channels));
                ffmpeg.av_frame_unref(_frame);
                int percent = (int)Math.Clamp(accumulator.Samples / (SampleRate * Duration) * 100, 0, 99);
                if (percent != lastProgress) { progress(percent); lastProgress = percent; }
                continue;
            }
            if (receive == ffmpeg.AVERROR_EOF) break;
            if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                if (!RecoverInvalidData(receive, ref invalidPackets)) Check(receive);
                if (draining) continue;
            }
            if (draining) throw new InvalidDataException("Decoder failed to drain.");
            int read;
            do
            {
                ffmpeg.av_packet_unref(_packet);
                read = ffmpeg.av_read_frame(_format, _packet);
            }
            while (read >= 0 && _packet->stream_index != _stream);
            if (read == ffmpeg.AVERROR_EOF)
            {
                draining = true;
                int flush = ffmpeg.avcodec_send_packet(_decoder, null);
                if (flush == ffmpeg.AVERROR_EOF) break;
                Check(flush);
            }
            else
            {
                Check(read);
                int sent = ffmpeg.avcodec_send_packet(_decoder, _packet);
                if (!RecoverInvalidData(sent, ref invalidPackets)) Check(sent);
            }
        }
        while (true)
        {
            int count;
            fixed (float* output = buffer)
            {
                byte* target = (byte*)output;
                count = ffmpeg.swr_convert(_resampler, &target, buffer.Length / channels, null, 0);
            }
            Check(count);
            if (count == 0) break;
            accumulator.Add(buffer.AsSpan(0, count * channels));
        }
        return accumulator.Finish(Channels);
    }

    private void RefreshDeadline() => _deadline.Ticks = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
    private static bool RecoverInvalidData(int code, ref int consecutiveErrors)
    {
        if (code != ffmpeg.AVERROR_INVALIDDATA) return false;
        // 某些 MP3 的 Lyrics3 尾标签会被 demuxer 当成音频包。丢弃无效包，继续读取或正常排空，
        // 保留已经解码的音频；限制连续错误，避免严重损坏文件在没有输出时反复尝试。
        if (++consecutiveErrors > 64) throw new InvalidDataException("Too many invalid audio packets.");
        return true;
    }
    private static void Check(int code) { if (code < 0) throw new InvalidDataException("FFmpeg error " + code); }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (SwrContext** resampler = &_resampler) ffmpeg.swr_free(resampler);
        fixed (AVCodecContext** decoder = &_decoder) ffmpeg.avcodec_free_context(decoder);
        fixed (AVFormatContext** format = &_format) ffmpeg.avformat_close_input(format);
        if (_handle.IsAllocated) _handle.Free();
    }
}
