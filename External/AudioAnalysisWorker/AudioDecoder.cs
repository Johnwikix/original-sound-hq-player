using System.Diagnostics;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
namespace WinUIMusicPlayer.AudioAnalysis;

/// <summary>本地只读解码器；拥有一个文件的原生上下文，使用调用方复用的浮点缓冲。</summary>
public sealed unsafe class AudioDecoder : IDisposable
{
    private sealed class Deadline { public long Ticks; }
    private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
        Stopwatch.GetTimestamp() >= ((Deadline)GCHandle.FromIntPtr((nint)opaque).Target!).Ticks ? 1 : 0;
    private readonly Deadline _deadline = new();
    private readonly float[] _scratch;
    private GCHandle _handle;
    private AVFormatContext* _format;
    private AVCodecContext* _decoder;
    private AVFrame* _frame;
    private AVPacket* _packet;
    private SwrContext* _swr;
    private int _index;
    private bool _disposed;
    /// <summary>获取文件元数据和技术字段。</summary>
    public double Duration { get; private set; }

    /// <summary>打开本地文件并配置单线程解码到32kHz单声道，不写音频文件。</summary>
    public AudioDecoder(string path, float[] scratch, bool readTags = false)
    {
        if (scratch.Length < 65536) throw new ArgumentException("Decode scratch too small.");
        _scratch = scratch;
        _handle = GCHandle.Alloc(_deadline);
        AVDictionary* options = null;
        AVChannelLayout mono = default;
        try
        {
            _deadline.Ticks = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
            _format = ffmpeg.avformat_alloc_context();
            if (_format == null) throw new OutOfMemoryException();
            _format->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = (void*)GCHandle.ToIntPtr(_handle) };
            _format->probesize = 1024 * 1024;
            _format->max_analyze_duration = 2 * ffmpeg.AV_TIME_BASE;
            _format->max_probe_packets = 128;
            ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
            fixed (AVFormatContext** format = &_format) Check(ffmpeg.avformat_open_input(format, path, null, &options));
            Check(ffmpeg.avformat_find_stream_info(_format, null));
            _index = ffmpeg.av_find_best_stream(_format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Check(_index);
            var stream = _format->streams[_index];
            var par = stream->codecpar;
            var codec = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (codec == null) throw new InvalidDataException("Audio decoder not found.");
            _decoder = ffmpeg.avcodec_alloc_context3(codec);
            if (_decoder == null) throw new OutOfMemoryException();
            Check(ffmpeg.avcodec_parameters_to_context(_decoder, par));
            _decoder->thread_count = 1;
            Check(ffmpeg.avcodec_open2(_decoder, codec, null));
            if (_decoder->sample_rate <= 0 || _decoder->ch_layout.nb_channels <= 0) throw new InvalidDataException("Invalid layout.");
            if (_decoder->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_UNSPEC)
            {
                int channels = _decoder->ch_layout.nb_channels;
                ffmpeg.av_channel_layout_uninit(&_decoder->ch_layout);
                ffmpeg.av_channel_layout_default(&_decoder->ch_layout, channels);
            }
            ffmpeg.av_channel_layout_default(&mono, 1);
            fixed (SwrContext** swr = &_swr)
                Check(ffmpeg.swr_alloc_set_opts2(swr, &mono, AVSampleFormat.AV_SAMPLE_FMT_FLT, 32000,
                    &_decoder->ch_layout, _decoder->sample_fmt, _decoder->sample_rate, 0, null));
            Check(ffmpeg.swr_init(_swr));
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
            if (_frame == null || _packet == null) throw new OutOfMemoryException();
            double duration = _format->duration > 0 ? _format->duration / (double)ffmpeg.AV_TIME_BASE
                : stream->duration > 0 ? stream->duration * ffmpeg.av_q2d(stream->time_base) : 0;
            Duration = duration;
        }
        catch { Dispose(); throw; }
        finally { ffmpeg.av_dict_free(&options); ffmpeg.av_channel_layout_uninit(&mono); }
    }
    /// <summary>从指定时间读取固定长度PCM，尾部不足时补零；返回真实读取样本数。</summary>
    public int ReadClip(double startSeconds, Span<float> destination)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AudioDecoder));
        destination.Clear();
        _deadline.Ticks = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
        var stream = _format->streams[_index];
        double streamStart = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * ffmpeg.av_q2d(stream->time_base);
        long timestamp = (long)Math.Round((startSeconds + streamStart) * ffmpeg.AV_TIME_BASE);
        Check(ffmpeg.av_seek_frame(_format, -1, timestamp, ffmpeg.AVSEEK_FLAG_BACKWARD));
        ffmpeg.avcodec_flush_buffers(_decoder);
        ffmpeg.av_frame_unref(_frame);
        ffmpeg.av_packet_unref(_packet);
        ffmpeg.swr_close(_swr);
        Check(ffmpeg.swr_init(_swr));
        int filled = 0;
        bool draining = false, first = true;
        long skip = 0;
        while (filled < destination.Length)
        {
            if (Stopwatch.GetTimestamp() >= _deadline.Ticks) throw new TimeoutException("Decode time budget exceeded.");
            int receive = ffmpeg.avcodec_receive_frame(_decoder, _frame);
            if (receive == 0)
            {
                if (first)
                {
                    first = false;
                    double frameStart = _frame->best_effort_timestamp == ffmpeg.AV_NOPTS_VALUE ? startSeconds
                        : _frame->best_effort_timestamp * ffmpeg.av_q2d(stream->time_base) - streamStart;
                    skip = Math.Max(0, (long)Math.Round((startSeconds - frameStart) * 32000));
                }
                int maximum = ffmpeg.swr_get_out_samples(_swr, _frame->nb_samples);
                if (maximum > _scratch.Length) throw new InvalidDataException("Decoded frame exceeds bounded scratch.");
                int count;
                fixed (float* output = _scratch)
                {
                    byte* target = (byte*)output;
                    count = ffmpeg.swr_convert(_swr, &target, _scratch.Length, _frame->extended_data, _frame->nb_samples);
                }
                Check(count);
                int dropped = (int)Math.Min(skip, count);
                skip -= dropped;
                int take = Math.Min(count - dropped, destination.Length - filled);
                _scratch.AsSpan(dropped, take).CopyTo(destination[filled..]);
                filled += take;
                ffmpeg.av_frame_unref(_frame);
                continue;
            }
            if (receive == ffmpeg.AVERROR_EOF) break;
            if (receive != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(receive);
            if (draining) break;
            int read;
            do
            {
                ffmpeg.av_packet_unref(_packet);
                read = ffmpeg.av_read_frame(_format, _packet);
            } while (read >= 0 && _packet->stream_index != _index);
            if (read == ffmpeg.AVERROR_EOF)
            {
                draining = true;
                Check(ffmpeg.avcodec_send_packet(_decoder, null));
            }
            else { Check(read); Check(ffmpeg.avcodec_send_packet(_decoder, _packet)); }
        }
        // .NET 11：调用方输入与解码scratch跨片段复用；不按帧申请托管数组。
        return filled;
    }
    private static void Check(int code)
    {
        if (code < 0) throw new InvalidDataException("FFmpeg error " + code);
    }
    /// <summary>在真实解码完成后释放所有原生上下文和回调根。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
        fixed (AVPacket** packet = &_packet) ffmpeg.av_packet_free(packet);
        fixed (SwrContext** swr = &_swr) ffmpeg.swr_free(swr);
        fixed (AVCodecContext** decoder = &_decoder) ffmpeg.avcodec_free_context(decoder);
        fixed (AVFormatContext** format = &_format) ffmpeg.avformat_close_input(format);
        if (_handle.IsAllocated) _handle.Free();
    }
}
