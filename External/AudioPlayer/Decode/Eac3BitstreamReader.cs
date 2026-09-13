using System.Buffers.Binary;
using FFmpeg.AutoGen;

namespace AudioPlayer.Decode;

/// <summary>E-AC-3 access units -> IEC 61937 bursts, preserving JOC bytes. No decode/re-encode.</summary>
internal sealed unsafe class Eac3BitstreamReader : IDisposable
{
    public const int BurstBytes = 24576;
    public const int CarrierRate = 192000;
    public const int CarrierFrameBytes = 4;
    public const int BurstFrames = BurstBytes / CarrierFrameBytes;
    private AVFormatContext* _format;
    private AVPacket* _packet;
    private int _stream;
    private long _startTimestamp;
    public long TotalMs { get; private set; }
    public bool IsAtmos { get; private set; }
    public uint EncodedChannelMask { get; private set; }

    public bool Open(string path)
    {
        Dispose();
        AVFormatContext* format = null;
        if (ffmpeg.avformat_open_input(&format, path, null, null) < 0) return false;
        _format = format;
        if (ffmpeg.avformat_find_stream_info(format, null) < 0) return false;
        _stream = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
        if (_stream < 0) return false;
        var codec = format->streams[_stream]->codecpar;
        // Initial experiment is deliberately limited to the 48kHz, six-channel DD+ delivery format.
        if (codec->codec_id != AVCodecID.AV_CODEC_ID_EAC3 || codec->sample_rate != 48000
            || codec->ch_layout.nb_channels != 6) return false;
        IsAtmos = codec->profile == ffmpeg.AV_PROFILE_EAC3_DDP_ATMOS;
        var stream = format->streams[_stream];
        _startTimestamp = (stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time)
            - ffmpeg.av_rescale_q(codec->initial_padding, new AVRational { num = 1, den = 48000 }, stream->time_base);
        EncodedChannelMask = codec->ch_layout.order == AVChannelOrder.AV_CHANNEL_ORDER_NATIVE
            && codec->ch_layout.u.mask is 0x3FUL or 0x60FUL ? (uint)codec->ch_layout.u.mask : 0x3F;
        TotalMs = format->duration > 0 ? (long)Math.Round(format->duration * 1000.0 / ffmpeg.AV_TIME_BASE) : 0;
        _packet = ffmpeg.av_packet_alloc();
        return _packet != null;
    }

    public int ReadBurst(Span<byte> destination)
    {
        if (destination.Length < BurstBytes) throw new ArgumentException("A full IEC 61937 burst is required.");
        destination = destination[..BurstBytes];
        destination.Clear();
        int bytes = 0, blocks = 0;
        while (_format != null && ffmpeg.av_read_frame(_format, _packet) >= 0)
        {
            try
            {
                if (_packet->stream_index != _stream) continue;
                var packet = new ReadOnlySpan<byte>(_packet->data, _packet->size);
                int packetBlocks = GetBlocks(packet);
                if (blocks + packetBlocks > 6 || bytes + packet.Length > BurstBytes - 8)
                    throw new InvalidDataException("Unsupported E-AC-3 access-unit grouping.");
                // IEC 61937 carries big-endian codec words in little-endian 16-bit carrier samples.
                for (int i = 0; i < packet.Length; i += 2)
                {
                    destination[8 + bytes + i] = packet[i + 1];
                    destination[8 + bytes + i + 1] = packet[i];
                }
                bytes += packet.Length;
                blocks += packetBlocks;
                if (blocks != 6) continue;
                BinaryPrimitives.WriteUInt16LittleEndian(destination, 0xF872);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], 0x4E1F);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], 0x15);
                BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], checked((ushort)bytes));
                return BurstBytes;
            }
            finally { ffmpeg.av_packet_unref(_packet); }
        }
        return 0;
    }

    internal static int GetBlocks(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 6 || (packet.Length & 1) != 0 || packet[0] != 0x0B || packet[1] != 0x77
            || (packet[5] >> 3) is < 11 or > 16 || (packet[4] & 0xC0) != 0
            || (packet[2] & 0xF8) != 0)
            throw new InvalidDataException("Expected a 48kHz E-AC-3 independent substream 0 access unit.");
        for (int offset = 0; offset < packet.Length;)
        {
            var frame = packet[offset..];
            if (frame.Length < 6 || frame[0] != 0x0B || frame[1] != 0x77
                || (frame[5] >> 3) is < 11 or > 16 || (frame[4] & 0xC0) != 0
                || (offset > 0 && (frame[2] >> 6) != 1))
                throw new InvalidDataException("Invalid E-AC-3 access-unit framing.");
            int frameBytes = ((((frame[2] & 7) << 8) | frame[3]) + 1) * 2;
            if (frameBytes < 6 || frameBytes > frame.Length) throw new InvalidDataException("Truncated E-AC-3 frame.");
            offset += frameBytes;
        }
        return ((packet[4] >> 4) & 3) switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };
    }

    public bool SeekToMs(long ms) => _format != null
        && (ms <= 0
            ? ffmpeg.av_seek_frame(_format, _stream, _startTimestamp, ffmpeg.AVSEEK_FLAG_BACKWARD)
            : ffmpeg.av_seek_frame(_format, -1, ms * 1000, ffmpeg.AVSEEK_FLAG_BACKWARD)) >= 0;

    public void Dispose()
    {
        if (_packet != null) { var packet = _packet; _packet = null; ffmpeg.av_packet_free(&packet); }
        if (_format != null) { var format = _format; _format = null; ffmpeg.avformat_close_input(&format); }
    }
}
