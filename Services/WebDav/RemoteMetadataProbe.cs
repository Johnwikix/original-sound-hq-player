using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using FFmpeg.AutoGen;

namespace WinUIMusicPlayer.Services.WebDav;

public sealed record RemoteMetadata(string Title, string Artist, string Album, int Track, int Disc, int Year,
    int SampleRate, int Channels, int BitDepth, int BitRate, double DurationMs);

/// <summary>FFmpeg 仅通过调用方的有界只读流取数据；原生回调不允许异常跨越 ABI。</summary>
public static unsafe class RemoteMetadataProbe
{
    private sealed class ReadState(Stream stream, CancellationToken token)
    {
        public Stream Stream { get; } = stream;
        public CancellationToken Token { get; } = token;
        public Exception? Failure;
    }
    private static readonly avio_alloc_context_read_packet Read = static (opaque, buffer, size) =>
    {
        var state = (ReadState)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
        try
        {
            state.Token.ThrowIfCancellationRequested();
            int count = state.Stream.Read(new Span<byte>(buffer, size));
            return count == 0 ? ffmpeg.AVERROR_EOF : count;
        }
        catch (Exception ex) { state.Failure = ex; return ffmpeg.AVERROR_EXIT; }
    };
    private static readonly avio_alloc_context_seek Seek = static (opaque, offset, origin) =>
    {
        var state = (ReadState)GCHandle.FromIntPtr((IntPtr)opaque).Target!;
        try
        {
            state.Token.ThrowIfCancellationRequested();
            if ((origin & ffmpeg.AVSEEK_SIZE) != 0) return state.Stream.Length;
            return state.Stream.Seek(offset, (SeekOrigin)(origin & ~ffmpeg.AVSEEK_FORCE));
        }
        catch (Exception ex) { state.Failure = ex; return -1; }
    };
    private static readonly AVIOInterruptCB_callback Interrupt = static opaque =>
        ((ReadState)GCHandle.FromIntPtr((IntPtr)opaque).Target!).Token.IsCancellationRequested ? 1 : 0;

    public static RemoteMetadata ReadMetadata(Stream input, string extension, CancellationToken token)
    {
        // FLAC 的 libavformat 路径会读出完整内嵌封面；ATL.Track 的图片是按需加载的。
        if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase)) return ReadFlacMetadata(input, token);
        var state = new ReadState(input, token);
        var handle = GCHandle.Alloc(state);
        AVFormatContext* format = null;
        AVIOContext* io = null;
        byte* buffer = null;
        try
        {
            buffer = (byte*)ffmpeg.av_malloc(32768);
            if (buffer == null) throw new OutOfMemoryException();
            io = ffmpeg.avio_alloc_context(buffer, 32768, 0, (void*)GCHandle.ToIntPtr(handle), Read, null, Seek);
            if (io == null) throw new OutOfMemoryException();
            buffer = null; // AVIO 接管；其 buffer 可能在探测中被替换。
            format = ffmpeg.avformat_alloc_context();
            if (format == null) throw new OutOfMemoryException();
            format->pb = io;
            format->flags |= ffmpeg.AVFMT_FLAG_CUSTOM_IO;
            format->probesize = 256 * 1024;
            format->max_analyze_duration = ffmpeg.AV_TIME_BASE;
            format->max_probe_packets = 64;
            format->interrupt_callback = new AVIOInterruptCB { callback = Interrupt, opaque = (void*)GCHandle.ToIntPtr(handle) };
            if (ffmpeg.avformat_open_input(&format, "remote" + extension, null, null) < 0 || format == null)
                throw state.Failure ?? new WebDavException("MetadataUnsupported");
            int index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (index < 0 || format->streams[index]->codecpar->sample_rate == 0)
            {
                ffmpeg.avformat_find_stream_info(format, null);
                index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            }
            if (state.Failure is not null) throw state.Failure;
            if (index < 0) throw new WebDavException("MetadataUnsupported");
            var stream = format->streams[index];
            var parameters = stream->codecpar;
            var containerTags = format->metadata;
            var streamTags = stream->metadata;
            string Field(string key)
            {
                var tag = ffmpeg.av_dict_get(containerTags, key, null, 0);
                if (tag == null) tag = ffmpeg.av_dict_get(streamTags, key, null, 0);
                return tag == null ? "" : Marshal.PtrToStringUTF8((IntPtr)tag->value) ?? "";
            }
            double duration = format->duration > 0 ? format->duration * 1000d / ffmpeg.AV_TIME_BASE
                : stream->duration > 0 ? stream->duration * ffmpeg.av_q2d(stream->time_base) * 1000 : 0;
            if (!double.IsFinite(duration) || duration < 0 || duration > TimeSpan.MaxValue.TotalMilliseconds) duration = 0;
            bool isDsd = IsDsdCodec(parameters->codec_id);
            // libavformat exposes DSF/DFF's byte rate as sample_rate (DSD bit rate / 8).
            // Music metadata and the rest of the app use the actual one-bit DSD rate.
            int sampleRate = isDsd && parameters->sample_rate > 0 && parameters->sample_rate <= int.MaxValue / 8
                ? parameters->sample_rate * 8 : parameters->sample_rate;
            int bits = isDsd ? 1 : parameters->bits_per_raw_sample > 0
                ? parameters->bits_per_raw_sample : ffmpeg.av_get_bits_per_sample(parameters->codec_id);
            return new(Field("title"), Field("artist"), Field("album"), Number(Field("track")), Number(Field("disc")),
                Number(Field("date")), sampleRate, parameters->ch_layout.nb_channels, bits,
                (int)Math.Clamp((format->bit_rate > 0 ? format->bit_rate : parameters->bit_rate) / 1000, 0, int.MaxValue), duration);
        }
        finally
        {
            if (format != null) ffmpeg.avformat_close_input(&format);
            if (io != null) { ffmpeg.av_free(io->buffer); ffmpeg.avio_context_free(&io); }
            if (buffer != null) ffmpeg.av_free(buffer);
            handle.Free();
        }
    }
    private static int Number(ReadOnlySpan<char> value)
    {
        int end = 0;
        while (end < value.Length && char.IsAsciiDigit(value[end])) end++;
        return int.TryParse(value[..end], out int number) ? number : 0;
    }
    private static RemoteMetadata ReadFlacMetadata(Stream input, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var file = new ATL.Track(input, ".flac");
        if (input is HttpRangeReadStream remote) remote.ThrowIfFailed();
        token.ThrowIfCancellationRequested();
        if (file.SampleRate <= 0 || !double.IsFinite(file.DurationMs)) throw new WebDavException("MetadataUnsupported");
        return new(file.Title ?? "", file.Artist ?? "", file.Album ?? "", file.TrackNumber ?? 0, file.DiscNumber ?? 0,
            file.Year ?? 0, (int)file.SampleRate, file.ChannelsArrangement.NbChannels,
            file.BitDepth, file.Bitrate, Math.Max(0, file.DurationMs));
    }

    private static bool IsDsdCodec(AVCodecID codec)
        => codec is AVCodecID.AV_CODEC_ID_DSD_LSBF or AVCodecID.AV_CODEC_ID_DSD_MSBF
            or AVCodecID.AV_CODEC_ID_DSD_LSBF_PLANAR or AVCodecID.AV_CODEC_ID_DSD_MSBF_PLANAR;
}
