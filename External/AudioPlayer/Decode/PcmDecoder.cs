using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

/// <summary>
/// FFmpeg PCM 解码层：avformat → avcodec → swresample → float64（double）交织。
/// swr 以 AV_SAMPLE_FMT_DBL 输出：重采样（共享模式改率）与 DSD→PCM 增益全程
/// double 域，下游 EQ/增益/输出转换吃满 float64 管线。
/// DSD 源（dsf/dff 的 DSD_* 编码，以及 bits_per_raw_sample==1 的 WavPack-DSD，
/// 共享输出/关闭位流/输出失败回退时）经解码器内置 DSD→PCM（输出率 = DSD 率/8）后再
/// 重采样到 dsdPcmFreq 并施加 DSD 增益——与库内 FFmpegAudioConverter 同一套套路。
/// 非托管资源只由所属解码线程访问（Open/Seek/Read 序列化调用）。
/// </summary>
internal sealed unsafe class PcmDecoder : IDisposable
{
    private GCHandle _interruptHandle;
    private CancellationToken _sourceToken;
    private int _ioCancelled;
    private long _deadline;
    private int _readTimeoutMs;
    public bool CanSeek { get; private set; }
    public void CancelIo() => Volatile.Write(ref _ioCancelled, 1);
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Interrupt(void* opaque)
    {
        var decoder = (PcmDecoder)GCHandle.FromIntPtr((nint)opaque).Target!;
        return Volatile.Read(ref decoder._ioCancelled) != 0 || decoder._sourceToken.IsCancellationRequested ||
            Environment.TickCount64 >= Volatile.Read(ref decoder._deadline) ? 1 : 0;
    }
    private void BeginRead() { if (_readTimeoutMs > 0) Volatile.Write(ref _deadline, Environment.TickCount64 + _readTimeoutMs); }
    private AVFormatContext* _fmt;
    private AVCodecContext* _dec;
    private SwrContext* _swr;
    private AVPacket* _pkt;
    private AVFrame* _frame;

    private int _streamIndex = -1;
    private bool _dsdSource;
    private double _dsdGainLinear = 1.0;
    private long _totalMs;
    private bool _eof;

    // 输出格式（重采样后）
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public uint ChannelMask { get; private set; }
    public long TotalMs => _totalMs;
    public bool IsDsdSource => _dsdSource;

    private static readonly AVCodecID[] DsdCodecIds =
    {
        AVCodecID.AV_CODEC_ID_DSD_LSBF,
        AVCodecID.AV_CODEC_ID_DSD_MSBF,
        AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR,
        AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR,
    };

    private static readonly int EAgain = ffmpeg.AVERROR(ffmpeg.EAGAIN);

    /// <param name="forceRate">强制输出采样率（独占/ASIO 回退共享时按混音率）；null = 源率。</param>
    /// <param name="forceChannels">强制输出声道数；null = 源声道数（受 maxChannels 上限约束）。</param>
    /// <param name="maxChannels">普通路径的声道上限；显式启用且识别出 5.1 时保留六声道，供自动独占输出。</param>
    public bool Open(string path, int dsdPcmFreq, int dsdGainDb, int? forceRate = null, int? forceChannels = null,
        int? maxChannels = null, bool experimentalSurround51 = false, PlaybackSource? source = null, CancellationToken cancellationToken = default)
    {
        try
        {
            AVFormatContext* fmt = null;
            AVDictionary* options = null;
            int or;
            try
            {
                if (source?.Kind == PlaybackSourceKind.Http)
                {
                    source.Validate();
                    _sourceToken = cancellationToken;
                    _readTimeoutMs = source.Buffer.ReadTimeoutMs;
                    _deadline = Environment.TickCount64 + source.Buffer.OpenTimeoutMs;
                    _interruptHandle = GCHandle.Alloc(this);
                    fmt = ffmpeg.avformat_alloc_context();
                    _fmt = fmt;
                    if (fmt == null) throw new OutOfMemoryException();
                    fmt->interrupt_callback.opaque = (void*)GCHandle.ToIntPtr(_interruptHandle);
                    fmt->interrupt_callback.callback = new AVIOInterruptCB_callback_func { Pointer = (nint)(delegate* unmanaged[Cdecl]<void*, int>)&Interrupt };
                    ffmpeg.av_dict_set(&options, "protocol_whitelist", "http,https,tcp,tls,httpproxy", 0);
                    ffmpeg.av_dict_set(&options, "tls_verify", "1", 0);
                    ffmpeg.av_dict_set(&options, "rw_timeout", ((long)source.Buffer.ReadTimeoutMs * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                    ffmpeg.av_dict_set(&options, "reconnect", source.Buffer.RetryCount > 0 ? "1" : "0", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_on_network_error", source.Buffer.RetryCount > 0 ? "1" : "0", 0);
                    ffmpeg.av_dict_set(&options, "reconnect_max_retries", source.Buffer.RetryCount.ToString(System.Globalization.CultureInfo.InvariantCulture), 0);
                    ffmpeg.av_dict_set(&options, "reconnect_delay_max", "2", 0);
                    if (source.Headers.Count > 0)
                        ffmpeg.av_dict_set(&options, "headers", string.Concat(source.Headers.Select(x => x.Key + ": " + x.Value + "\r\n")), 0);
                }
                or = ffmpeg.avformat_open_input(&fmt, path, null, &options);
                _fmt = fmt;
            }
            finally { ffmpeg.av_dict_free(&options); }
            if (or < 0 || fmt == null) { Console.WriteLine($"[decode] open_input ret={or}"); return false; }
            _fmt = fmt;
            CanSeek = (source?.CanSeek ?? true) && (fmt->pb == null || (fmt->pb->seekable & ffmpeg.AVIO_SEEKABLE_NORMAL) != 0);
            int fsr = ffmpeg.avformat_find_stream_info(_fmt, null);
            if (fsr < 0) { Console.WriteLine($"[decode] find_stream_info ret={fsr}"); return false; }

            int si = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (si < 0) { Console.WriteLine($"[decode] find_best_stream ret={si}"); return false; }
            _streamIndex = si;
            AVCodecParameters* par = _fmt->streams[si]->codecpar;

            int codecId = (int)par->codec_id;
            bool dsdContainer = Array.IndexOf(DsdCodecIds, par->codec_id) >= 0;
            bool wvDsd = par->codec_id == AVCodecID.AV_CODEC_ID_WAVPACK && par->bits_per_raw_sample == 1;
            _dsdSource = dsdContainer || wvDsd;
            _dsdGainLinear = _dsdSource && dsdGainDb != 0 ? Math.Pow(10.0, dsdGainDb / 20.0) : 1.0;

            AVCodec* decoder = ffmpeg.avcodec_find_decoder(par->codec_id);
            if (decoder == null) { Console.WriteLine($"[decode] no decoder codec={par->codec_id}"); return false; }
            _dec = ffmpeg.avcodec_alloc_context3(decoder);
            ffmpeg.avcodec_parameters_to_context(_dec, par);
            int or2 = ffmpeg.avcodec_open2(_dec, decoder, null);
            if (or2 < 0) { Console.WriteLine($"[decode] avcodec_open2 ret={or2}"); return false; }

            // WAV/PCM 解码器常给 UNSPEC 布局，swr 需规范化（转换器同款注释）
            int channels = Math.Max(1, _dec->ch_layout.nb_channels);
            bool surround51 = experimentalSurround51 && !_dsdSource && channels == 6
                && forceChannels == null
                && _dec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE
                && _dec->ch_layout.u.mask is 0x3FUL or 0x60FUL;
            ChannelMask = surround51 ? (uint)_dec->ch_layout.u.mask : 0;
            if (!surround51)
            {
                ffmpeg.av_channel_layout_uninit(&_dec->ch_layout);
                ffmpeg.av_channel_layout_default(&_dec->ch_layout, channels);
            }

            int inRate = _dec->sample_rate != 0 ? _dec->sample_rate : 48000;
            SampleRate = forceRate ?? (_dsdSource && dsdPcmFreq > 0 ? dsdPcmFreq : inRate);
            // A recognized 5.1 layout opts into automatic exclusive output; other layouts keep the ordinary channel limit.
            Channels = surround51 ? 6 : forceChannels ?? (maxChannels is > 0 ? Math.Min(channels, maxChannels.Value) : channels);

            AVChannelLayout outLayout = default;
            if (surround51) ffmpeg.av_channel_layout_copy(&outLayout, &_dec->ch_layout);
            else ffmpeg.av_channel_layout_default(&outLayout, Channels);
            SwrContext* swr = null;
            ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_DBL, SampleRate,
                &_dec->ch_layout, _dec->sample_fmt, _dec->sample_rate, 0, null);
            ffmpeg.av_channel_layout_uninit(&outLayout);
            _swr = swr;
            int sir = _swr == null ? -1 : ffmpeg.swr_init(_swr);
            if (sir < 0) { Console.WriteLine($"[decode] swr_init ret={sir}"); return false; }

            _pkt = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _totalMs = _fmt->duration > 0 ? (long)Math.Round(_fmt->duration / (double)ffmpeg.AV_TIME_BASE * 1000) : 0;
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[decode] Open failed: {ex.GetType().Name}");
            return false;
        }
    }

    /// <summary>seek（BACKWARD 到关键包），清空解码器与重采样器。仅解码线程调用。</summary>
    public bool SeekToMs(long ms)
    {
        if (_fmt == null || _dec == null || !CanSeek) return false;
        BeginRead();
        if (ffmpeg.av_seek_frame(_fmt, -1, ms * 1000, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0) return false;
        ffmpeg.avcodec_flush_buffers(_dec);
        ffmpeg.swr_init(_swr);
        _eof = false;
        return true;
    }

    /// <summary>读取交织 double 块。返回帧数；0 = 本位置流结束（EOF 后恒 0）。</summary>
    public int Read(Span<double> buffer)
    {
        if (_fmt == null || _dec == null) return 0;
        int maxFrames = buffer.Length / Math.Max(1, Channels);
        if (maxFrames <= 0) return 0;

        while (true)
        {
            // 1) 先排空重采样器内部缓冲
            int got = ConvertOut(buffer, maxFrames);
            if (got > 0) return got;

            if (_eof) return 0;

            // 2) 取一帧并喂给重采样器
            int ret = ffmpeg.avcodec_receive_frame(_dec, _frame);
            if (ret == 0)
            {
                fixed (double* p = buffer)
                {
                    got = ffmpeg.swr_convert(_swr, (byte**)&p, maxFrames,
                        _frame->extended_data, _frame->nb_samples);
                }
                ffmpeg.av_frame_unref(_frame);
                if (got > 0) return FinishRead(buffer, got);
                continue; // 帧被重采样器内部吸收（改率延迟），继续取下一帧
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                _eof = true;
                ffmpeg.avcodec_send_packet(_dec, null); // 幂等冲洗
                // 冲洗重采样器
                got = ConvertOut(buffer, maxFrames);
                return got; // 剩余样本一次给足（缓冲区足够大，通常一次排空）
            }
            if (ret != EAgain) throw new IOException($"Decoder error {ret}.");

            // 3) 需要新输入包
            BeginRead();
            int pr = ffmpeg.av_read_frame(_fmt, _pkt);
            if (pr == EAgain) continue;
            if (pr < 0 && pr != ffmpeg.AVERROR_EOF) throw new IOException($"Input read error {pr}.");
            if (pr == ffmpeg.AVERROR_EOF)
            {
                // Only confirmed EOF drains the decoder; a transport failure is not a track end.
                ffmpeg.avcodec_send_packet(_dec, null);
                continue;
            }
            if (_pkt->stream_index == _streamIndex)
                ffmpeg.avcodec_send_packet(_dec, _pkt);
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    private int ConvertOut(Span<double> buffer, int frames)
    {
        int got;
        fixed (double* p = buffer)
        {
            got = ffmpeg.swr_convert(_swr, (byte**)&p, frames, null, 0);
        }
        return got > 0 ? FinishRead(buffer, got) : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int FinishRead(Span<double> buffer, int frames)
    {
        if (_dsdGainLinear != 1f)
        {
            int n = frames * Channels;
            for (int i = 0; i < n; i++) buffer[i] *= _dsdGainLinear;
        }
        return frames;
    }

    public void Dispose()
    {
        if (_frame != null) { AVFrame* f = _frame; _frame = null; ffmpeg.av_frame_free(&f); }
        if (_pkt != null) { AVPacket* p = _pkt; _pkt = null; ffmpeg.av_packet_free(&p); }
        if (_swr != null) { SwrContext* s = _swr; _swr = null; ffmpeg.swr_free(&s); }
        if (_dec != null) { AVCodecContext* d = _dec; _dec = null; ffmpeg.avcodec_free_context(&d); }
        if (_fmt != null) { AVFormatContext* f2 = _fmt; _fmt = null; ffmpeg.avformat_close_input(&f2); }
        if (_interruptHandle.IsAllocated) _interruptHandle.Free();
    }
}
