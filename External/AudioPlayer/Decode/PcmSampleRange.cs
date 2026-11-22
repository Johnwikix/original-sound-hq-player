using System;
using System.IO;
using System.Runtime.InteropServices;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

namespace AudioDecoding.Shared;

/// <summary>Discards preroll and crops decoded PCM/FLAC before resampling.</summary>
internal sealed unsafe class PcmSampleRange
{
    private readonly AudioSampleRange _range;
    private readonly long _streamStart;
    private long _nextSample;

    internal PcmSampleRange(AudioSampleRange range, AVFormatContext* format, AVStream* stream, bool canSeek)
    {
        range.Validate();
        var parameters = stream->codecpar;
        string? container = Marshal.PtrToStringAnsi((nint)format->iformat->name);
        var codec = ffmpeg.avcodec_descriptor_get(parameters->codec_id);
        bool supported = container == "flac" && parameters->codec_id == AVCodecID.AV_CODEC_ID_FLAC
            || container == "wav" && codec != null && Marshal.PtrToStringAnsi((nint)codec->name)?.StartsWith("pcm_", StringComparison.Ordinal) == true;
        if (!canSeek || !supported || parameters->sample_rate != range.SampleRate
            || (long)stream->time_base.num * range.SampleRate != stream->time_base.den
            || stream->duration < range.EndSample)
            throw new IOException("The audio stream cannot provide the requested sample range.");

        _range = range;
        _streamStart = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        _nextSample = range.StartSample;
    }

    internal bool EndReached => _nextSample == _range.EndSample;

    internal bool Seek(AVFormatContext* format, int streamIndex, long sample)
    {
        sample = Math.Clamp(sample, _range.StartSample, _range.EndSample);
        // Seeking to the exclusive endpoint needs no input packet, even at physical EOF.
        if (sample != _range.EndSample)
        {
            // Unindexed FLAC may fail to resolve a timestamp near EOF. Retry from earlier
            // packets and discard preroll using decoded timestamps, keeping the target exact.
            long preroll = 0;
            while (ffmpeg.av_seek_frame(format, streamIndex, checked(_streamStart + sample - preroll), ffmpeg.AVSEEK_FLAG_BACKWARD) < 0)
            {
                if (preroll == sample) return false;
                preroll = preroll == 0 ? Math.Min(sample, _range.SampleRate)
                    : preroll > sample / 4 ? sample : preroll * 4;
            }
        }
        _nextSample = sample;
        return true;
    }

    internal void Crop(AVFrame* frame, out int skip, out int take)
    {
        skip = take = 0;
        if (EndReached) return;
        long timestamp = frame->best_effort_timestamp;
        if (timestamp == ffmpeg.AV_NOPTS_VALUE || frame->sample_rate != _range.SampleRate)
            throw new IOException("A decoded frame did not report its source sample position.");

        long start = checked(timestamp - _streamStart);
        long end = checked(start + frame->nb_samples);
        if (end <= _nextSample) return;
        if (start > _nextSample)
            throw new IOException("Decoded audio skipped samples in the requested range.");

        skip = (int)(_nextSample - start);
        take = (int)Math.Min(end - _nextSample, _range.EndSample - _nextSample);
        _nextSample += take;
    }

    internal static byte** OffsetInput(AVFrame* frame, int skip, byte** offsetData)
    {
        if (skip == 0) return frame->extended_data;
        var format = (AVSampleFormat)frame->format;
        int bytes = ffmpeg.av_get_bytes_per_sample(format);
        int channels = frame->ch_layout.nb_channels;
        if (ffmpeg.av_sample_fmt_is_planar(format) != 0)
        {
            for (int channel = 0; channel < channels; channel++)
                offsetData[channel] = frame->extended_data[channel] + checked(skip * bytes);
        }
        else
        {
            offsetData[0] = frame->extended_data[0] + checked(skip * bytes * channels);
        }
        return offsetData;
    }
}
