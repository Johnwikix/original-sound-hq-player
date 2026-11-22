using System;
using System.IO;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;

namespace AudioDecoding.Shared;

/// <summary>Discards preroll and crops the lossless source before resampling.</summary>
internal sealed unsafe class PcmSampleRange
{
    private readonly AudioSampleRange _range;
    private readonly long _streamStart;
    private readonly bool _ape;
    private long _nextSample;
    private long _endSample;
    private long? _decodedEnd;

    internal PcmSampleRange(AudioSampleRange range, AVFormatContext* format, AVStream* stream, string path, bool canSeek)
    {
        range.Validate();
        if (!canSeek || stream->codecpar->sample_rate != range.SampleRate
            || !CuePcmSource.TryGetSampleCount(format, stream, path, out long totalSamples)
            || totalSamples < range.EndSample)
            throw new IOException("The audio stream cannot provide the requested sample range.");

        _range = range;
        _streamStart = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time;
        _ape = stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_APE;
        _nextSample = range.StartSample;
        _endSample = range.EndSample;
    }

    internal bool EndReached => _nextSample == _endSample;

    internal bool Seek(AVFormatContext* format, int streamIndex, long sample, long? endSample = null)
    {
        long end = endSample ?? _range.EndSample;
        if (end < _range.StartSample || end > _range.EndSample)
            throw new ArgumentOutOfRangeException(nameof(endSample));
        sample = Math.Clamp(sample, _range.StartSample, end);
        // Seeking to the exclusive endpoint needs no input packet, even at physical EOF.
        if (sample != end)
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
        _decodedEnd = null;
        _nextSample = sample;
        _endSample = end;
        return true;
    }

    internal void Crop(AVFrame* frame, out int skip, out int take)
    {
        skip = take = 0;
        if (EndReached) return;
        long timestamp = frame->best_effort_timestamp;
        if (frame->sample_rate != _range.SampleRate || frame->nb_samples <= 0)
            throw new IOException("A decoded frame did not match the source sample rate.");

        // The APE decoder emits multiple frames per packet. Only the first frame carries the packet timestamp.
        long start = timestamp != ffmpeg.AV_NOPTS_VALUE ? checked(timestamp - _streamStart)
            : _ape && _decodedEnd is { } previousEnd ? previousEnd
            : throw new IOException("A decoded frame did not report its source sample position.");
        long end = checked(start + frame->nb_samples);
        _decodedEnd = end;
        if (end <= _nextSample) return;
        if (start > _nextSample)
            throw new IOException("Decoded audio skipped samples in the requested range.");

        skip = (int)(_nextSample - start);
        take = (int)Math.Min(end - _nextSample, _endSample - _nextSample);
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
