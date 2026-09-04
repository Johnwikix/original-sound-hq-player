using System;
using System.IO;
using System.Text;
using FFmpeg.AutoGen;

namespace WinUIMusicPlayer.AudioConverters
{
    /// <summary>
    /// 基于 FFmpeg(libavformat/libavcodec/libswresample) DLL 的进程内音频转换器，
    /// 取代旧的 BASS(bassenc) 转换管线：解码 → swresample 重采样/位深/DSD 增益 → 编码封装。
    /// 位深决策与旧版 BASS 转换器保持一致：
    ///   wav  : 24bit 源 → 24bit PCM；16bit/未知(非 DSD) → 16bit；其余(32bit/float/DSD) → float；
    ///   flac : ≥24bit 或 DSD → 24bit，否则 16bit；
    ///   mp3/ogg/opus : 320 kbps，编码器原生采样格式。
    /// </summary>
    public sealed unsafe class FFmpegAudioConverter
    {
        /// <summary>转换进度(0-100)，在转换线程上回调。</summary>
        public EventHandler<double>? progressEvent;

        // 单次 Convert 期间的有效字段（供编码回调取包，避免闭包捕获指针）
        private AVCodecContext* _encCtx;
        private AVFormatContext* _ofmtCtx;
        private AVStream* _outStream;
        private AVPacket* _pkt;
        private int _outRate;

        private const long LossyBitRate = 320_000;
        private const int FlacCompressionLevel = 8; // 对应旧版 libflac "--best"
        private static readonly int[] OpusRates = [8000, 12000, 16000, 24000, 48000];

        public void Convert(string inputPath, string outputPath, string format, int dsdPcmFreq = 0, int dsdGainDb = 0)
        {
            bool isDsd = IsDsdFile(inputPath);
            bool applyGain = isDsd && dsdGainDb != 0;
            double gainLinear = applyGain ? Math.Pow(10, dsdGainDb / 20.0) : 1.0;

            AVFormatContext* inFmt = null;
            AVCodecContext* decCtx = null;
            AVFormatContext* ofmtCtx = null;
            AVCodecContext* encCtx = null;
            SwrContext* swr = null;
            SwrContext* gainSwr = null;
            AVPacket* pkt = ffmpeg.av_packet_alloc();
            AVFrame* decFrame = ffmpeg.av_frame_alloc();
            AVFrame* swrFrame = ffmpeg.av_frame_alloc();
            AVFrame* gainFrame = ffmpeg.av_frame_alloc();
            EncoderFrameFeeder? chunker = null;

            try
            {
                int ret = ffmpeg.avformat_open_input(&inFmt, inputPath, null, null);
                if (ret < 0) throw CreateException(ret, $"无法打开音频文件: {inputPath}");
                if (ffmpeg.avformat_find_stream_info(inFmt, null) < 0)
                    throw new InvalidOperationException($"无法读取流信息: {inputPath}");

                int streamIndex = ffmpeg.av_find_best_stream(inFmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (streamIndex < 0) throw new InvalidOperationException($"文件中没有音频流: {inputPath}");
                AVStream* inStream = inFmt->streams[streamIndex];

                AVCodec* decoder = ffmpeg.avcodec_find_decoder(inStream->codecpar->codec_id);
                if (decoder == null) throw new InvalidOperationException($"没有可用解码器: {inStream->codecpar->codec_id}");
                decCtx = ffmpeg.avcodec_alloc_context3(decoder);
                ffmpeg.avcodec_parameters_to_context(decCtx, inStream->codecpar);
                if (ffmpeg.avcodec_open2(decCtx, decoder, null) < 0)
                    throw new InvalidOperationException("解码器打开失败");

                // WAV/PCM 解码器常给出 UNSPEC 顺序布局；swr_init 会把 UNSPEC 规范化成
                // 原生默认布局，导致 swr_convert_frame 的配置比较报 INPUT_CHANGED。
                // 解码帧继承 avctx 布局，这里统一规范化为一劳永逸。
                int channels = Math.Max(1, decCtx->ch_layout.nb_channels);
                ffmpeg.av_channel_layout_uninit(&decCtx->ch_layout);
                ffmpeg.av_channel_layout_default(&decCtx->ch_layout, channels);

                int depth = DetectBitDepth(decCtx, isDsd);
                int inRate = decCtx->sample_rate != 0 ? decCtx->sample_rate : 48000;
                int outRate = isDsd && dsdPcmFreq > 0 ? dsdPcmFreq : inRate;

                SelectEncoder(format, depth, isDsd, ref outRate,
                    out string muxerName, out AVCodecID encCodecId, out AVSampleFormat encFmt, out int flacBps);

                ffmpeg.avformat_alloc_output_context2(&ofmtCtx, null, muxerName, outputPath);
                if (ofmtCtx == null) throw new InvalidOperationException($"不支持的输出格式: {format}");

                AVCodec* encoder = ffmpeg.avcodec_find_encoder(encCodecId);
                if (encoder == null) throw new InvalidOperationException($"没有可用编码器: {encCodecId}");

                encCtx = ffmpeg.avcodec_alloc_context3(encoder);
                encCtx->sample_fmt = encFmt;
                encCtx->sample_rate = outRate;
                encCtx->bit_rate = encCodecId == AVCodecID.AV_CODEC_ID_FLAC ? 0 : LossyBitRate;
                ffmpeg.av_channel_layout_copy(&encCtx->ch_layout, &decCtx->ch_layout);
                // lame 等编码器只接受原生声道布局；解码器可能是 UNSPEC 顺序，统一规范化
                ffmpeg.av_channel_layout_uninit(&encCtx->ch_layout);
                ffmpeg.av_channel_layout_default(&encCtx->ch_layout, Math.Max(1, decCtx->ch_layout.nb_channels));
                // 采样率对齐编码器支持表（lame 最高 48k 等），避免 DSD 高采样率直接失败
                outRate = SnapRateToEncoder(encCtx, encoder, outRate);
                encCtx->sample_rate = outRate;
                if (flacBps > 0) encCtx->bits_per_raw_sample = flacBps;
                if (encCodecId == AVCodecID.AV_CODEC_ID_FLAC) encCtx->compression_level = FlacCompressionLevel;
                if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                    encCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

                if (ffmpeg.avcodec_open2(encCtx, encoder, null) < 0)
                    throw new InvalidOperationException("编码器打开失败");

                AVStream* outStream = ffmpeg.avformat_new_stream(ofmtCtx, null);
                outStream->time_base = new AVRational { num = 1, den = outRate };
                ffmpeg.avcodec_parameters_from_context(outStream->codecpar, encCtx);

                if ((ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                {
                    ret = ffmpeg.avio_open(&ofmtCtx->pb, outputPath, ffmpeg.AVIO_FLAG_WRITE);
                    if (ret < 0) throw CreateException(ret, $"无法创建输出文件: {outputPath}");
                }
                if (ffmpeg.avformat_write_header(ofmtCtx, null) < 0)
                    throw new InvalidOperationException("写入文件头失败");

                // 第一级重采样：解码格式 → 编码格式（DSD 需增益且目标为整型时先到 float，
                // 由第二级 gainSwr 完成 float → 整型，保证增益在浮点域施加）。
                bool gainNeedsFloatStage = applyGain && !IsFloatFormat(encFmt);
                AVSampleFormat primaryFmt = gainNeedsFloatStage ? AVSampleFormat.AV_SAMPLE_FMT_FLT : encFmt;

                AVChannelLayout inLayout = default;
                ffmpeg.av_channel_layout_copy(&inLayout, &decCtx->ch_layout);
                ffmpeg.swr_alloc_set_opts2(&swr, &encCtx->ch_layout, primaryFmt, outRate,
                    &inLayout, decCtx->sample_fmt, decCtx->sample_rate, 0, null);
                if (swr == null) throw new InvalidOperationException("swresample 创建失败");
                if (ffmpeg.swr_init(swr) < 0) throw new InvalidOperationException("swresample 初始化失败");

                if (gainNeedsFloatStage)
                {
                    ffmpeg.swr_alloc_set_opts2(&gainSwr, &encCtx->ch_layout, encFmt, outRate,
                        &encCtx->ch_layout, AVSampleFormat.AV_SAMPLE_FMT_FLT, outRate, 0, null);
                    if (gainSwr == null || ffmpeg.swr_init(gainSwr) < 0)
                        throw new InvalidOperationException("DSD 增益级初始化失败");
                }

                chunker = new EncoderFrameFeeder(encCtx, DrainEncodedPackets);
                double totalSeconds = inFmt->duration > 0 ? inFmt->duration / (double)ffmpeg.AV_TIME_BASE : 0;
                long samplesWritten = 0;
                int lastPercent = -1;

                // 每帧编码后需要立即取包写出，编码器内部缓冲有限，积压会返回 EAGAIN
                _encCtx = encCtx;
                _ofmtCtx = ofmtCtx;
                _outStream = outStream;
                _outRate = outRate;
                _pkt = pkt;

                while ((ret = ffmpeg.av_read_frame(inFmt, pkt)) >= 0)
                {
                    if (pkt->stream_index == streamIndex)
                    {
                        ffmpeg.avcodec_send_packet(decCtx, pkt);
                        while (ffmpeg.avcodec_receive_frame(decCtx, decFrame) >= 0)
                        {
                            ConvertFrame(decFrame, swr, gainSwr, gainFrame, swrFrame,
                                applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                                chunker, ref samplesWritten);
                        }
                    }
                    ffmpeg.av_packet_unref(pkt);
                    ReportProgress(totalSeconds, samplesWritten, outRate, ref lastPercent);
                }

                // 冲洗解码器
                ffmpeg.avcodec_send_packet(decCtx, null);
                while (ffmpeg.avcodec_receive_frame(decCtx, decFrame) >= 0)
                {
                    ConvertFrame(decFrame, swr, gainSwr, gainFrame, swrFrame,
                        applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                        chunker, ref samplesWritten);
                }

                // 冲洗两级 swresample 的延迟缓冲
                FlushSwr(swr, gainSwr, gainFrame, swrFrame,
                    applyGain, gainLinear, primaryFmt, encFmt, outRate, &encCtx->ch_layout,
                    chunker, ref samplesWritten);

                chunker.Flush();

                // 冲洗编码器并写出剩余包
                ffmpeg.avcodec_send_frame(encCtx, null);
                while (ffmpeg.avcodec_receive_packet(encCtx, pkt) >= 0)
                {
                    WritePacket(pkt, ofmtCtx, outStream, outRate);
                }

                ffmpeg.av_write_trailer(ofmtCtx);
                ReportProgress(totalSeconds, samplesWritten, outRate, ref lastPercent, force: true);
            }
            finally
            {
                chunker?.Dispose();
                ffmpeg.av_frame_free(&gainFrame);
                ffmpeg.av_frame_free(&swrFrame);
                ffmpeg.av_frame_free(&decFrame);
                ffmpeg.av_packet_free(&pkt);
                if (swr != null) ffmpeg.swr_free(&swr);
                if (gainSwr != null) ffmpeg.swr_free(&gainSwr);
                if (encCtx != null) ffmpeg.avcodec_free_context(&encCtx);
                if (ofmtCtx != null)
                {
                    if (ofmtCtx->pb != null && (ofmtCtx->oformat->flags & ffmpeg.AVFMT_NOFILE) == 0)
                        ffmpeg.avio_closep(&ofmtCtx->pb);
                    ffmpeg.avformat_free_context(ofmtCtx);
                }
                if (decCtx != null) ffmpeg.avcodec_free_context(&decCtx);
                if (inFmt != null) ffmpeg.avformat_close_input(&inFmt);
            }
        }

        // ──────────────── 内部管线 ────────────────

        private void ConvertFrame(AVFrame* decFrame, SwrContext* swr, SwrContext* gainSwr, AVFrame* gainFrame,
            AVFrame* swrFrame, bool applyGain, double gainLinear, AVSampleFormat primaryFmt, AVSampleFormat encFmt,
            int outRate, AVChannelLayout* outLayout, EncoderFrameFeeder chunker, ref long samplesWritten)
        {
            PrepareFrame(swrFrame, primaryFmt, outRate, outLayout);
            int ret = ffmpeg.swr_convert_frame(swr, swrFrame, decFrame);
            if (ret < 0) throw CreateException(ret, "重采样失败");
            if (swrFrame->nb_samples == 0) return;

            samplesWritten += swrFrame->nb_samples;

            if (gainSwr != null)
            {
                ApplyGain(swrFrame, gainLinear); // 浮点域施加 DSD 增益
                PrepareFrame(gainFrame, encFmt, outRate, outLayout);
                ret = ffmpeg.swr_convert_frame(gainSwr, gainFrame, swrFrame);
                if (ret < 0) throw CreateException(ret, "DSD 增益级转换失败");
                if (gainFrame->nb_samples > 0) chunker.Push(gainFrame);
            }
            else
            {
                if (applyGain) ApplyGain(swrFrame, gainLinear);
                chunker.Push(swrFrame);
            }
        }

        private void FlushSwr(SwrContext* swr, SwrContext* gainSwr, AVFrame* gainFrame, AVFrame* swrFrame,
            bool applyGain, double gainLinear, AVSampleFormat primaryFmt, AVSampleFormat encFmt,
            int outRate, AVChannelLayout* outLayout, EncoderFrameFeeder chunker, ref long samplesWritten)
        {
            while (true)
            {
                PrepareFrame(swrFrame, primaryFmt, outRate, outLayout);
                int ret = ffmpeg.swr_convert_frame(swr, swrFrame, null);
                if (ret < 0 || swrFrame->nb_samples == 0) break;

                samplesWritten += swrFrame->nb_samples;
                if (gainSwr != null)
                {
                    ApplyGain(swrFrame, gainLinear);
                    while (true)
                    {
                        PrepareFrame(gainFrame, encFmt, outRate, outLayout);
                        ret = ffmpeg.swr_convert_frame(gainSwr, gainFrame, swrFrame);
                        if (ret < 0 || gainFrame->nb_samples == 0) break;
                        chunker.Push(gainFrame);
                        if (ffmpeg.swr_get_out_samples(gainSwr, 0) <= 0) break;
                    }
                }
                else
                {
                    if (applyGain) ApplyGain(swrFrame, gainLinear);
                    chunker.Push(swrFrame);
                }
                if (ffmpeg.swr_get_out_samples(swr, 0) <= 0) break;
            }
        }

        /// <summary>av_frame_unref 会清空全部字段，重设 swr_convert_frame 要求的输出帧参数。</summary>
        private static void PrepareFrame(AVFrame* frame, AVSampleFormat fmt, int outRate, AVChannelLayout* outLayout)
        {
            ffmpeg.av_frame_unref(frame);
            frame->format = (int)fmt;
            frame->sample_rate = outRate;
            ffmpeg.av_channel_layout_copy(&frame->ch_layout, outLayout);
        }

        private static void ApplyGain(AVFrame* frame, double linear)
        {
            if (frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLT)
            {
                float* p = (float*)frame->data[0];
                int n = frame->nb_samples * frame->ch_layout.nb_channels;
                for (int i = 0; i < n; i++) p[i] = (float)(p[i] * linear);
            }
            else if (frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP)
            {
                for (int ch = 0; ch < frame->ch_layout.nb_channels; ch++)
                {
                    float* p = (float*)frame->data[(uint)ch];
                    for (int i = 0; i < frame->nb_samples; i++) p[i] = (float)(p[i] * linear);
                }
            }
        }

        private void WritePacket(AVPacket* pkt, AVFormatContext* ofmtCtx, AVStream* stream, int encRate)
        {
            ffmpeg.av_packet_rescale_ts(pkt,
                new AVRational { num = 1, den = encRate },
                stream->time_base);
            pkt->stream_index = stream->index;
            ffmpeg.av_interleaved_write_frame(ofmtCtx, pkt);
            ffmpeg.av_packet_unref(pkt);
        }

        /// <summary>每帧送入编码器后立即取空输出包并写入封装器。</summary>
        private void DrainEncodedPackets()
        {
            while (ffmpeg.avcodec_receive_packet(_encCtx, _pkt) >= 0)
            {
                WritePacket(_pkt, _ofmtCtx, _outStream, _outRate);
            }
        }

        private void ReportProgress(double totalSeconds, long samplesWritten, int outRate, ref int lastPercent, bool force = false)
        {
            if (progressEvent == null) return;
            int percent;
            if (totalSeconds <= 0)
                percent = force ? 100 : (lastPercent < 0 ? 0 : lastPercent);
            else
                percent = (int)Math.Clamp(samplesWritten / (double)outRate / totalSeconds * 100, 0, 100);
            if (force || percent != lastPercent)
            {
                lastPercent = percent;
                progressEvent?.Invoke(this, percent);
            }
        }

        private static bool IsDsdFile(string path)
        {
            var ext = Path.GetExtension(path.AsSpan());
            return ext.Equals(".dsf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".dff", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFloatFormat(AVSampleFormat fmt)
            => fmt is AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_FLTP;

        /// <summary>把采样率对齐到编码器支持表中最接近的一档；编码器未声明则原样返回。</summary>
        private static int SnapRateToEncoder(AVCodecContext* encCtx, AVCodec* encoder, int rate)
        {
            void* cfg = null;
            int count = 0;
            int ret = ffmpeg.avcodec_get_supported_config(encCtx, encoder,
                AVCodecConfig.AV_CODEC_CONFIG_SAMPLE_RATE, 0, &cfg, &count);
            if (ret < 0 || cfg == null || count <= 0) return rate;
            int* rates = (int*)cfg;
            int best = 0, bestDiff = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int diff = Math.Abs(rates[i] - rate);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    best = rates[i];
                }
            }
            return best != 0 ? best : rate;
        }

        private static InvalidOperationException CreateException(int errorCode, string message)
        {
            const int BufSize = 512;
            byte* buf = stackalloc byte[BufSize];
            ffmpeg.av_strerror(errorCode, buf, (ulong)BufSize);
            int len = 0;
            while (len < BufSize && buf[len] != 0) len++;
            return new InvalidOperationException($"{message} (FFmpeg 错误 {errorCode}: {Encoding.ASCII.GetString(buf, len)})");
        }

        /// <summary>源位深：优先解码器 bits_per_raw_sample（FLAC/DSD 容器会给出），其次按编码器推断；DSD 恒为 1。</summary>
        private static int DetectBitDepth(AVCodecContext* decCtx, bool isDsd)
        {
            if (isDsd) return 1;
            if (decCtx->bits_per_raw_sample > 0) return decCtx->bits_per_raw_sample;
            return decCtx->codec_id switch
            {
                AVCodecID.AV_CODEC_ID_PCM_S16LE or AVCodecID.AV_CODEC_ID_PCM_S16BE
                    or AVCodecID.AV_CODEC_ID_PCM_U8 => 16,
                AVCodecID.AV_CODEC_ID_PCM_S24LE or AVCodecID.AV_CODEC_ID_PCM_S24BE => 24,
                AVCodecID.AV_CODEC_ID_PCM_S32LE or AVCodecID.AV_CODEC_ID_PCM_S32BE
                    or AVCodecID.AV_CODEC_ID_PCM_F32LE or AVCodecID.AV_CODEC_ID_PCM_F32BE => 32,
                AVCodecID.AV_CODEC_ID_PCM_F64LE or AVCodecID.AV_CODEC_ID_PCM_F64BE => 64,
                _ => 0,
            };
        }

        /// <summary>按旧版 BASS 规则选择封装器/编码器/采样格式与输出采样率。</summary>
        private static void SelectEncoder(string format, int depth, bool isDsd, ref int outRate,
            out string muxerName, out AVCodecID encCodecId, out AVSampleFormat encFmt, out int flacBps)
        {
            flacBps = 0;
            switch (format)
            {
                case "wav":
                    muxerName = "wav";
                    if (depth == 24)
                    {
                        encCodecId = AVCodecID.AV_CODEC_ID_PCM_S24LE;
                        encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32; // pcm_s24le 以 s32 承载（高 24 位有效）
                    }
                    else if ((depth == 0 || depth == 16) && !isDsd)
                    {
                        encCodecId = AVCodecID.AV_CODEC_ID_PCM_S16LE;
                        encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                    }
                    else
                    {
                        encCodecId = AVCodecID.AV_CODEC_ID_PCM_F32LE;
                        encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;
                    }
                    return;
                case "flac":
                    muxerName = "flac";
                    encCodecId = AVCodecID.AV_CODEC_ID_FLAC;
                    if (depth >= 24 || isDsd)
                    {
                        encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32;
                        flacBps = 24;
                    }
                    else
                    {
                        encFmt = AVSampleFormat.AV_SAMPLE_FMT_S16;
                        flacBps = 16;
                    }
                    return;
                case "mp3":
                    muxerName = "mp3";
                    encCodecId = AVCodecID.AV_CODEC_ID_MP3;
                    encFmt = AVSampleFormat.AV_SAMPLE_FMT_S32P; // libmp3lame 首选格式
                    return;
                case "ogg":
                    muxerName = "ogg";
                    encCodecId = AVCodecID.AV_CODEC_ID_VORBIS;
                    encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP; // libvorbis 仅支持 fltp
                    return;
                case "opus":
                    muxerName = "opus";
                    encCodecId = AVCodecID.AV_CODEC_ID_OPUS;
                    encFmt = AVSampleFormat.AV_SAMPLE_FMT_FLT; // libopus 接受 packed fmt
                    // Opus 只工作在 8/12/16/24/48 kHz
                    int best = OpusRates[0];
                    foreach (var r in OpusRates)
                        if (Math.Abs(r - outRate) < Math.Abs(best - outRate)) best = r;
                    outRate = best;
                    return;
                default:
                    throw new InvalidOperationException($"不支持的输出格式: {format}");
            }
        }

        /// <summary>
        /// 固定 frame_size 编码器（如 libopus 960 采样）的分帧供料器：
        /// 把任意大小的转换帧切成编码器要求的精确帧长，结尾发送剩余短帧并冲刷。
        /// </summary>
        private sealed unsafe class EncoderFrameFeeder : IDisposable
        {
            private readonly AVCodecContext* _encCtx;
            private readonly Action _drain;
            private readonly AVFrame* _frame;
            private readonly bool _planar;
            private readonly int _bytesPerSample;
            private readonly int _channels;
            private readonly int _capacity;
            private int _filled;
            private long _nextPts;

            public EncoderFrameFeeder(AVCodecContext* encCtx, Action drain)
            {
                _encCtx = encCtx;
                _drain = drain;
                _capacity = encCtx->frame_size > 0 ? encCtx->frame_size : 0;
                _bytesPerSample = ffmpeg.av_get_bytes_per_sample(encCtx->sample_fmt);
                _channels = encCtx->ch_layout.nb_channels;
                _planar = ffmpeg.av_sample_fmt_is_planar(encCtx->sample_fmt) != 0;

                _frame = ffmpeg.av_frame_alloc();
                _frame->format = (int)encCtx->sample_fmt;
                _frame->sample_rate = encCtx->sample_rate;
                ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &encCtx->ch_layout);
                _frame->nb_samples = _capacity > 0 ? _capacity : 1152;
                if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0)
                    throw new InvalidOperationException("编码帧缓冲分配失败");
            }

            public void Push(AVFrame* src)
            {
                if (_capacity == 0)
                {
                    // 编码器接受任意帧长（lame/vorbis/pcm/flac），直通
                    src->pts = _nextPts;
                    _nextPts += src->nb_samples;
                    Send(src);
                    return;
                }
                int srcOffset = 0;
                while (srcOffset < src->nb_samples)
                {
                    int copy = Math.Min(src->nb_samples - srcOffset, _capacity - _filled);
                    CopySamples(src, srcOffset, _filled, copy);
                    _filled += copy;
                    srcOffset += copy;
                    if (_filled == _capacity)
                    {
                        _frame->nb_samples = _capacity;
                        _frame->pts = _nextPts;
                        _nextPts += _capacity;
                        Send(_frame);
                        ReallocFrame();
                    }
                }
            }

            public void Flush()
            {
                if (_capacity > 0 && _filled > 0)
                {
                    _frame->nb_samples = _filled;
                    _frame->pts = _nextPts;
                    Send(_frame);
                    ReallocFrame();
                }
            }

            private void Send(AVFrame* frame)
            {
                int ret = ffmpeg.avcodec_send_frame(_encCtx, frame);
                if (ret < 0) throw CreateException(ret, "编码失败");
                _drain();
            }

            private void ReallocFrame()
            {
                ffmpeg.av_frame_unref(_frame);
                _frame->format = (int)_encCtx->sample_fmt;
                _frame->sample_rate = _encCtx->sample_rate;
                ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &_encCtx->ch_layout);
                _frame->nb_samples = _capacity > 0 ? _capacity : 1152;
                if (ffmpeg.av_frame_get_buffer(_frame, 0) < 0)
                    throw new InvalidOperationException("编码帧缓冲分配失败");
                _filled = 0;
            }

            private void CopySamples(AVFrame* src, int srcStart, int dstStart, int count)
            {
                if (_planar)
                {
                    for (int ch = 0; ch < _channels; ch++)
                    {
                        Buffer.MemoryCopy(
                            src->extended_data[ch] + (long)srcStart * _bytesPerSample,
                            _frame->data[(uint)ch] + (long)dstStart * _bytesPerSample,
                            (long)count * _bytesPerSample,
                            (long)count * _bytesPerSample);
                    }
                }
                else
                {
                    Buffer.MemoryCopy(
                        src->extended_data[0] + (long)srcStart * _channels * _bytesPerSample,
                        _frame->data[0] + (long)dstStart * _channels * _bytesPerSample,
                        (long)count * _channels * _bytesPerSample,
                        (long)count * _channels * _bytesPerSample);
                }
            }

            public void Dispose()
            {
                fixed (AVFrame** pp = &_frame)
                {
                    ffmpeg.av_frame_free(pp);
                }
            }
        }
    }
}
