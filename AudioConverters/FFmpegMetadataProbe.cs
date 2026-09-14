using System;
using System.Diagnostics;
using System.IO;
using FFmpeg.AutoGen;

namespace WinUIMusicPlayer.AudioConverters
{
    /// <summary>
    /// FFmpeg 有界元数据探测（find_stream_info 可能读取并解码少量数据包）：
    /// GetAudioInfo 兜底链的末级，仅在 ATL 与 Windows 属性系统都拿不到技术字段时使用。
    /// 只补时长/采样率/声道/位深/码率，不解析标签（Title/Artist/Album 由 ATL/属性系统/文件名负责）。
    /// 每次调用独立 AVFormatContext，可与转换器并发（libavformat 上下文级隔离）。
    /// </summary>
    public static unsafe class FFmpegMetadataProbe
    {
        // Root the native callback; its opaque deadline lives until avformat_close_input returns.
        private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
            Stopwatch.GetTimestamp() >= *(long*)opaque ? 1 : 0;

        public readonly record struct ProbeResult(
            int SampleRate,
            int Channels,
            int BitDepth,
            int BitRateKbps,
            double DurationMs);

        /// <summary>成功打开容器并读到至少一项有效技术字段时返回 true。</summary>
        public static bool TryProbe(string path, out ProbeResult result)
        {
            result = default;
            if (!Path.IsPathFullyQualified(path)) return false;
            AVFormatContext* fmt = null;
            AVDictionary* options = null;
            long deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            try
            {
                fmt = ffmpeg.avformat_alloc_context();
                if (fmt == null) return false;
                fmt->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = &deadline };
                fmt->probesize = 1024 * 1024;
                fmt->max_analyze_duration = 2 * ffmpeg.AV_TIME_BASE;
                fmt->max_probe_packets = 128;
                ffmpeg.av_dict_set(&options, "protocol_whitelist", "file", 0);
                if (ffmpeg.avformat_open_input(&fmt, path, null, &options) < 0 || fmt == null)
                    return false;
                if (ffmpeg.avformat_find_stream_info(fmt, null) < 0)
                    return false;
                int si = ffmpeg.av_find_best_stream(fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
                if (si < 0)
                    return false;

                AVStream* stream = fmt->streams[si];
                AVCodecParameters* par = stream->codecpar;

                int sampleRate = par->sample_rate;
                int channels = par->ch_layout.nb_channels;
                // 有损流常无 raw sample 信息，保持 0 让调用方沿用现有默认，不猜值
                int bitDepth = par->bits_per_raw_sample > 0 ? par->bits_per_raw_sample
                    : ffmpeg.av_get_bits_per_sample(par->codec_id);
                long containerBitRate = fmt->bit_rate > 0 ? fmt->bit_rate : par->bit_rate;
                int bitRateKbps = containerBitRate > 0 ? (int)Math.Min(int.MaxValue, Math.Round(containerBitRate / 1000.0)) : 0;

                double durationMs = 0;
                if (fmt->duration > 0)
                    durationMs = fmt->duration * 1000.0 / ffmpeg.AV_TIME_BASE;
                else if (stream->duration > 0)
                    durationMs = stream->duration * ffmpeg.av_q2d(stream->time_base) * 1000.0;

                if (!double.IsFinite(durationMs) || durationMs < 0 || durationMs >= TimeSpan.MaxValue.TotalMilliseconds)
                    durationMs = 0;
                result = new ProbeResult(sampleRate, channels, bitDepth, bitRateKbps, durationMs);
                return sampleRate > 0 || channels > 0 || durationMs > 0;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                if (options != null) ffmpeg.av_dict_free(&options);
                if (fmt != null)
                    ffmpeg.avformat_close_input(&fmt);
            }
        }
    }
}
