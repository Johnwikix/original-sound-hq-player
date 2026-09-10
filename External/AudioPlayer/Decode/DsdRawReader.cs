using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

/// <summary>
/// 原始 DSD 位流读取器（DoP / Native DSD 透传用）。
/// WV-DSD 由 libwavpack 解压为原始位流；DSF/DFF 走下面的 FFmpeg 解复用路径。
/// 只用 libavformat 解复用，不经解码器：镜像 FFmpeg DSD 解码器 repack() 的输入模型——
/// dsf/dff demuxer 输出的包是纯 DSD 字节（DSF=每声道平面块拼接，DSDIFF=交织），
/// repack 为「MSB 优先、每帧每声道 1 字节」的交织字节流。
/// FFmpeg 的 codecpar->sample_rate 为 PCM 域（DSD 率/8），据此推导：
/// DoP 帧率 = sample_rate/2（每 DoP 采样含 16 个 DSD 位），Native DSD 位率 = sample_rate*8。
/// </summary>
internal sealed unsafe class DsdRawReader : IDisposable
{
    private AVFormatContext* _fmt;
    private AVPacket* _pkt;
    private int _streamIndex = -1;
    private WavPackDsdReader? _wavpack;

    /// <summary>每声道每秒 DSD 字节数（= FFmpeg 上报的 sample_rate，即 DSD 位率/8）。</summary>
    public int ByteRatePerChannel { get; private set; }
    public int Channels { get; private set; }
    public long TotalMs { get; private set; }
    /// <summary>DoP 采样率（= ByteRatePerChannel/2）。</summary>
    public int DopSampleRate => ByteRatePerChannel / 2;
    /// <summary>Native DSD 位率（DSD64=2822400）。</summary>
    public int DsdBitRate => ByteRatePerChannel * 8;

    public bool Open(string path)
    {
        Dispose();
        try
        {
            if (Path.GetExtension(path).Equals(".wv", StringComparison.OrdinalIgnoreCase))
            {
                _wavpack = new WavPackDsdReader();
                if (!_wavpack.Open(path)) return false;
                ByteRatePerChannel = _wavpack.ByteRatePerChannel;
                Channels = _wavpack.Channels;
                TotalMs = _wavpack.TotalMs;
                return true;
            }
            AVFormatContext* fmt = null;
            if (ffmpeg.avformat_open_input(&fmt, path, null, null) < 0 || fmt == null) return false;
            _fmt = fmt;
            if (ffmpeg.avformat_find_stream_info(_fmt, null) < 0) return false;

            int si = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (si < 0) return false;
            _streamIndex = si;
            AVCodecParameters* par = _fmt->streams[si]->codecpar;

            if (!IsRawDsdCodec(par->codec_id)) return false;

            ByteRatePerChannel = par->sample_rate > 0 ? par->sample_rate : 0;
            Channels = par->ch_layout.nb_channels > 0 ? par->ch_layout.nb_channels : 2;
            _planar = par->codec_id is AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR or AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR;
            _lsbFirst = par->codec_id is AVCodecID.AV_CODEC_ID_DSD_LSBF or AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR;
            if (ByteRatePerChannel <= 0) return false;

            _pkt = ffmpeg.av_packet_alloc();
            TotalMs = _fmt->duration > 0 ? (long)Math.Round(_fmt->duration / (double)ffmpeg.AV_TIME_BASE * 1000) : 0;
            if (TotalMs <= 0 && par->block_align > 0 && _fmt->streams[si]->duration > 0)
            {
                // 由流时长（时基 1/sample_rate，单位=每声道字节）推总时长
                TotalMs = (long)Math.Round(_fmt->streams[si]->duration / (double)ByteRatePerChannel * 1000);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRawDsdCodec(AVCodecID id)
        => id is AVCodecID.AV_CODEC_ID_DSD_LSBF or AVCodecID.AV_CODEC_ID_DSD_MSBF
            or AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR or AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR;

    private bool _planar;
    private bool _lsbFirst;

    private static readonly byte[] BitReverse = BuildBitReverse();

    private static byte[] BuildBitReverse()
    {
        var table = new byte[256];
        for (int i = 0; i < 256; i++)
        {
            int v = i, r = 0;
            for (int b = 0; b < 8; b++) { r = (r << 1) | (v & 1); v >>= 1; }
            table[i] = (byte)r;
        }
        return table;
    }

    /// <summary>seek 到目标毫秒（流时基单位 = 每声道字节，包粒度对齐）。仅解码线程调用。</summary>
    public bool SeekToMs(long ms)
    {
        if (_wavpack != null) return _wavpack.SeekToMs(ms);
        if (_fmt == null) return false;
        double seconds = ms / 1000.0;
        long target = (long)Math.Round(seconds * ByteRatePerChannel); // 每声道字节位置 = pts
        if (ffmpeg.av_seek_frame(_fmt, _streamIndex, target, ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            return false;
        return true;
    }

    /// <summary>
    /// 读一个包并 repack 为「MSB 优先交织」DSD 字节（每帧每声道 1 字节）。
    /// 返回写入的字节数；0 = EOF。dest 需 ≥ packet size。
    /// </summary>
    public int ReadInterleaved(Span<byte> dest)
    {
        if (_wavpack != null) return _wavpack.ReadInterleaved(dest);
        if (_fmt == null) return 0;
        while (true)
        {
            if (ffmpeg.av_read_frame(_fmt, _pkt) < 0) return 0;
            if (_pkt->stream_index == _streamIndex) break;
            ffmpeg.av_packet_unref(_pkt);
        }

        int size = _pkt->size;
        if (size <= 0) { ffmpeg.av_packet_unref(_pkt); return 0; }
        int channels = Channels;
        int planeBytes = size / channels;
        if (planeBytes <= 0) { ffmpeg.av_packet_unref(_pkt); return 0; }

        byte* src = _pkt->data;
        int outBytes = planeBytes * channels;

        if (!_planar)
        {
            // DSDIFF：已交织
            if (_lsbFirst)
            {
                for (int i = 0; i < outBytes && i < dest.Length; i++)
                    dest[i] = BitReverse[src[i]];
            }
            else
            {
                new Span<byte>(src, Math.Min(outBytes, dest.Length)).CopyTo(dest);
            }
        }
        else
        {
            // DSF：平面块 → 交织；LSB 源先反位序
            for (int ch = 0; ch < channels; ch++)
            {
                byte* plane = src + ch * planeBytes;
                for (int i = 0; i < planeBytes; i++)
                {
                    int idx = i * channels + ch;
                    if (idx >= dest.Length) break;
                    dest[idx] = _lsbFirst ? BitReverse[plane[i]] : plane[i];
                }
            }
        }

        ffmpeg.av_packet_unref(_pkt);
        // 防御：超大包被截断时按实际写入量返回（调用方按返回值消费 scratch）
        return Math.Min(outBytes, dest.Length);
    }

    public void Dispose()
    {
        _wavpack?.Dispose();
        _wavpack = null;
        if (_pkt != null) { AVPacket* p = _pkt; _pkt = null; ffmpeg.av_packet_free(&p); }
        if (_fmt != null) { AVFormatContext* f = _fmt; _fmt = null; ffmpeg.avformat_close_input(&f); }
    }
}
