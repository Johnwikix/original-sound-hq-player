using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace AudioDecoding.Shared;

internal static unsafe class CuePcmSource
{
    internal static void SetInputOptions(AVDictionary** options)
    {
        // CUE indexes refer to physical samples. A MOV edit list can trim or move them
        // on a movie timeline whose timescale does not resolve individual audio samples.
        if (ffmpeg.av_dict_set(options, "ignore_editlist", "1", 0) < 0)
            throw new IOException("Could not configure the CUE source timeline.");
    }

    internal static bool TryGetSampleCount(AVFormatContext* format, AVStream* stream, string path, out long samples) =>
        TryGetSampleCount(format, stream, path, out samples, out _);

    internal static bool TryGetSampleCount(AVFormatContext* format, AVStream* stream, string path, out long samples, out int headerBitDepth)
    {
        samples = 0;
        headerBitDepth = 0;
        var parameters = stream->codecpar;
        if (parameters->sample_rate <= 0 || parameters->ch_layout.nb_channels <= 0
            || (long)stream->time_base.num * parameters->sample_rate != stream->time_base.den
            || stream->duration <= 0 || format->pb == null
            || (format->pb->seekable & ffmpeg.AVIO_SEEKABLE_NORMAL) == 0
            || ffmpeg.avcodec_find_decoder(parameters->codec_id) == null)
            return false;

        var count = stream->duration;
        string? container = Marshal.PtrToStringAnsi((nint)format->iformat->name);
        var codec = parameters->codec_id;
        bool supported = container switch
        {
            "flac" => codec == AVCodecID.AV_CODEC_ID_FLAC,
            "wav" => IsWavePcm(codec),
            "aiff" => IsLinearPcm(codec) && HasAiffSampleData(path, count, parameters->block_align, out headerBitDepth),
            "ape" => codec == AVCodecID.AV_CODEC_ID_APE && HasCompleteIndex(format, stream),
            "wv" => codec == AVCodecID.AV_CODEC_ID_WAVPACK && HasLosslessWavPackHeader(path, count, out headerBitDepth),
            "mov,mp4,m4a,3gp,3g2,mj2" => codec == AVCodecID.AV_CODEC_ID_ALAC
                && HasSingleAudioStream(format) && HasCompleteIndex(format, stream),
            _ => false
        };
        if (!supported) return false;
        samples = count;
        headerBitDepth = codec switch
        {
            AVCodecID.AV_CODEC_ID_PCM_F32BE or AVCodecID.AV_CODEC_ID_PCM_F32LE => 32,
            AVCodecID.AV_CODEC_ID_PCM_F64BE or AVCodecID.AV_CODEC_ID_PCM_F64LE => 64,
            _ when container == "aiff" && headerBitDepth > ffmpeg.av_get_bits_per_sample(codec) => 0,
            _ => headerBitDepth
        };
        return true;
    }

    private static bool IsWavePcm(AVCodecID codec)
    {
        var descriptor = ffmpeg.avcodec_descriptor_get(codec);
        return descriptor != null && Marshal.PtrToStringAnsi((nint)descriptor->name)?.StartsWith("pcm_", StringComparison.Ordinal) == true;
    }

    private static bool IsLinearPcm(AVCodecID codec) => codec is
        AVCodecID.AV_CODEC_ID_PCM_S8 or AVCodecID.AV_CODEC_ID_PCM_U8
        or AVCodecID.AV_CODEC_ID_PCM_S16BE or AVCodecID.AV_CODEC_ID_PCM_S16LE
        or AVCodecID.AV_CODEC_ID_PCM_S24BE or AVCodecID.AV_CODEC_ID_PCM_S24LE
        or AVCodecID.AV_CODEC_ID_PCM_S32BE or AVCodecID.AV_CODEC_ID_PCM_S32LE
        or AVCodecID.AV_CODEC_ID_PCM_F32BE or AVCodecID.AV_CODEC_ID_PCM_F32LE
        or AVCodecID.AV_CODEC_ID_PCM_F64BE or AVCodecID.AV_CODEC_ID_PCM_F64LE;

    private static bool HasAiffSampleData(string path, long samples, int blockAlign, out int bitDepth)
    {
        bitDepth = 0;
        if (blockAlign <= 0) return false;
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        long formEnd = BinaryPrimitives.ReadUInt32BigEndian(header[4..]) + 8L;
        if (formEnd > file.Length || !header[..4].SequenceEqual("FORM"u8)
            || !(header[8..].SequenceEqual("AIFF"u8) || header[8..].SequenceEqual("AIFC"u8)))
            return false;

        bool foundSamples = false;
        while (file.Position <= formEnd - 8)
        {
            file.ReadExactly(header[..8]);
            long length = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            long end = file.Position + length;
            if (end + (length & 1) > formEnd) return false;
            if (header[..4].SequenceEqual("SSND"u8))
            {
                if (length < 8) return false;
                file.ReadExactly(header[..8]);
                long offset = BinaryPrimitives.ReadUInt32BigEndian(header);
                if (samples > (length - 8 - offset) / blockAlign) return false;
                foundSamples = true;
            }
            else if (header[..4].SequenceEqual("COMM"u8))
            {
                if (length < 18) return false;
                file.ReadExactly(header[..8]);
                bitDepth = BinaryPrimitives.ReadUInt16BigEndian(header[6..]);
            }
            if (foundSamples && bitDepth > 0) return true;
            file.Position = end + (length & 1);
        }
        return foundSamples;
    }

    private static bool HasLosslessWavPackHeader(string path, long samples, out int bitDepth)
    {
        const uint hybridFlag = 0x8;
        const uint floatFlag = 0x80;
        const uint dsdFlag = 0x80000000;
        bitDepth = 0;
        using var file = File.OpenRead(path);
        Span<byte> header = stackalloc byte[32];
        while (file.Position <= file.Length - header.Length)
        {
            long start = file.Position;
            file.ReadExactly(header);
            long blockBytes = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) + 8L;
            if (!header[..4].SequenceEqual("wvpk"u8) || blockBytes < header.Length || blockBytes > file.Length - start)
                return false;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header[20..]) != 0)
            {
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(header[24..]);
                uint total = BinaryPrimitives.ReadUInt32LittleEndian(header[12..]);
                // The shift field counts the unused bits within integer PCM storage.
                bitDepth = (flags & floatFlag) != 0 ? 32 : ((int)(flags & 3) + 1) * 8 - (int)((flags >> 13) & 0x1F);
                // This FFmpeg demuxer reads 32-bit sample numbers. WavPack 5 uses the
                // previous track/index bytes for the high bits of the 40-bit values.
                return (flags & (hybridFlag | dsdFlag)) == 0 && header[10] == 0 && header[11] == 0
                    && BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) == 0
                    && total != uint.MaxValue && total == samples;
            }
            file.Position = start + blockBytes;
        }
        return false;
    }

    private static bool HasSingleAudioStream(AVFormatContext* format)
    {
        int audio = 0;
        for (int i = 0; i < format->nb_streams; i++)
            if (format->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO && ++audio > 1)
                return false;
        return audio == 1;
    }

    private static bool HasCompleteIndex(AVFormatContext* format, AVStream* stream)
    {
        int entries = ffmpeg.avformat_index_get_entries_count(stream);
        if (entries <= 0 || entries != stream->nb_frames) return false;
        var first = ffmpeg.avformat_index_get_entry(stream, 0);
        var last = ffmpeg.avformat_index_get_entry(stream, entries - 1);
        long fileBytes = ffmpeg.avio_size(format->pb);
        return first != null && last != null && first->timestamp == 0 && first->pos >= 0 && last->pos >= first->pos
            && last->timestamp >= 0 && last->timestamp < stream->duration && last->pos < fileBytes;
    }
}
