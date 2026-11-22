using AudioDecoding.Shared;
using AudioPlayer.Decode;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.AudioConverters;

internal static unsafe partial class Program
{
    private static void RunCueFormatFixtures()
    {
        foreach (string extension in new[] { "ape", "m4a", "wv" })
            RunCueFormatTests(Path.Combine(AppContext.BaseDirectory, "CueFixtures", $"Image.{extension}"), null);

        var work = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"cue-waveforms-{Guid.NewGuid():N}"));
        try
        {
            foreach (var (bits, floatingPoint) in new[] { (32, false), (32, true), (64, true) })
            {
                string path = Path.Combine(work.FullName, $"{bits}-{(floatingPoint ? "float" : "integer")}.wav");
                WriteCuePrecisionFixture(path, bits, floatingPoint);
                RunCueFormatTests(path, null);
            }
        }
        finally { work.Delete(true); }
    }

    private static void WriteCuePrecisionFixture(string path, int bits, bool floatingPoint)
    {
        const int sampleRate = 48000;
        const int frames = sampleRate * 2 + 137;
        int bytes = bits / 8;
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * bytes);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((ushort)(floatingPoint ? 3 : 1));
        writer.Write((ushort)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * bytes);
        writer.Write((ushort)bytes);
        writer.Write((ushort)bits);
        writer.Write("data"u8);
        writer.Write(frames * bytes);
        for (int i = 0; i < frames; i++)
        {
            double value = Math.Sin(i * 0.037) * Math.Cos(i * 0.0079);
            if (bits == 64) writer.Write(value * 1.05);
            else if (floatingPoint) writer.Write((float)(value * 1.05));
            else writer.Write((int)(value * int.MaxValue));
        }
    }

    // Supply an independently decoded PCM image if the container has a separate presentation timeline.
    private static void RunCueFormatTests(string source, string? referencePath)
    {
        var (sampleRate, totalSamples, floatingPoint) = ReadCueFormatTimeline(source);
        Require(totalSamples >= sampleRate * 2L, "A reference image with at least two seconds is required.");
        long midpoint = totalSamples / 2;
        var ranges = new AudioSampleRange[]
        {
            new(113, Math.Min(410003, totalSamples), sampleRate),
            new(midpoint + 227, midpoint + 16013, sampleRate),
            new(totalSamples - 16013, totalSamples, sampleRate),
        };
        var (channels, reference) = ReadCueFormatReference(referencePath ?? source, ranges, totalSamples);
        var work = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"cue-formats-{Guid.NewGuid():N}"));
        try
        {
            var name = Path.GetExtension(source);
            for (int i = 0; i < ranges.Length; i++)
            {
                var input = new AudioInput(source, ranges[i]);
                double[] expected = reference[i];
                int index = i;
                Run($"CUE {name} clip {index + 1}: exact samples, seek, export and analysis", () =>
                    CheckCueFormatPaths(input, expected, channels, floatingPoint, Path.Combine(work.FullName, $"clip-{index}")));
            }
        }
        finally { work.Delete(true); }
    }

    private static (int Rate, long Samples, bool FloatingPoint) ReadCueFormatTimeline(string path)
    {
        AVFormatContext* format = null;
        AVDictionary* options = null;
        try
        {
            CuePcmSource.SetInputOptions(&options);
            Require(ffmpeg.avformat_open_input(&format, path, null, &options) >= 0, "Cannot open the test image.");
            Require(ffmpeg.avformat_find_stream_info(format, null) >= 0, "Cannot read stream information.");
            int index = ffmpeg.av_find_best_stream(format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            Require(index >= 0, "Test image has no audio stream.");
            AVStream* stream = format->streams[index];
            Require(CuePcmSource.TryGetSampleCount(format, stream, path, out long count), "Test image cannot be split.");
            var sampleFormat = ffmpeg.av_get_packed_sample_fmt((AVSampleFormat)stream->codecpar->format);
            return (stream->codecpar->sample_rate, count, sampleFormat is AVSampleFormat.AV_SAMPLE_FMT_FLT or AVSampleFormat.AV_SAMPLE_FMT_DBL);
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
            ffmpeg.avformat_close_input(&format);
        }
    }

    private static (int Channels, double[][] Windows) ReadCueFormatReference(string path, AudioSampleRange[] ranges, long totalSamples)
    {
        using var decoder = new PcmDecoder();
        Require(decoder.Open(path, 88200, 0) && decoder.SampleRate == ranges[0].SampleRate, "Cannot open the sequential reference.");
        int channels = decoder.Channels;
        var expected = ranges.Select(range => new double[checked((int)range.FrameCount * channels)]).ToArray();
        double[] buffer = new double[8192 * channels];
        long position = 0;
        int frames;
        while ((frames = decoder.Read(buffer)) > 0)
        {
            for (int i = 0; i < ranges.Length; i++)
            {
                long start = Math.Max(position, ranges[i].StartSample);
                long end = Math.Min(position + frames, ranges[i].EndSample);
                if (start < end)
                    buffer.AsSpan((int)(start - position) * channels, (int)(end - start) * channels)
                        .CopyTo(expected[i].AsSpan((int)(start - ranges[i].StartSample) * channels));
            }
            position += frames;
        }
        Require(position == totalSamples, $"Reference returned {position} PCM frames, expected {totalSamples}.");
        return (channels, expected);
    }

    private static void CheckCueFormatPaths(AudioInput input, double[] reference, int channels, bool floatingPoint, string exportStem)
    {
        var range = input.SampleRange!.Value;
        using var playback = new PcmDecoder();
        Require(playback.Open(input.Path, 88200, 0, sampleRange: range) && playback.Channels == channels
            && playback.TotalMs == range.DurationMs, "Player could not open the clip.");
        CompareCueFormatSamples(playback, reference);
        const int seekMs = 113;
        Require(playback.SeekToMs(seekMs), "Player could not seek after the clip ended.");
        int offset = (int)(range.SampleAt(seekMs) - range.StartSample) * channels;
        CompareCueFormatSamples(playback, reference.AsSpan(offset));
        Require(playback.SeekToMs(range.DurationMs) && playback.Read(new double[channels]) == 0,
            "Seek to the exclusive end read past the clip.");

        string wav = exportStem + ".wav";
        CheckCueFormatExport(input, reference, channels, wav, "wav");
        if (!floatingPoint && channels <= 8)
            CheckCueFormatExport(input, reference, channels, exportStem + ".flac", "flac");

        var referenceInput = new AudioInput(wav, new(0, range.FrameCount, range.SampleRate));
        using var analyzed = new AudioDecoder(input, new float[65536]);
        using var analyzedReference = new AudioDecoder(referenceInput, new float[65536]);
        float[] analysis = new float[8192], expectedAnalysis = new float[8192];
        foreach (double start in new[] { 0, 0.173, 0.029 })
        {
            int count = analyzed.ReadClip(start, analysis);
            int expectedCount = analyzedReference.ReadClip(start, expectedAnalysis);
            Require(count == expectedCount && analysis.AsSpan().SequenceEqual(expectedAnalysis), "MIR window differs from the exported PCM.");
        }

        using var spectrogram = new SpectrogramDecoder(input);
        using var spectrogramReference = new SpectrogramDecoder(referenceInput);
        var spectrum = spectrogram.Analyze(2048, _ => { });
        var expectedSpectrum = spectrogramReference.Analyze(2048, _ => { });
        Require(spectrum.Duration == expectedSpectrum.Duration && spectrum.Width == expectedSpectrum.Width
            && spectrum.Combined.AsSpan().SequenceEqual(expectedSpectrum.Combined)
            && spectrum.Left.AsSpan().SequenceEqual(expectedSpectrum.Left)
            && spectrum.Right.AsSpan().SequenceEqual(expectedSpectrum.Right),
            "Spectrogram differs from the exported PCM.");
    }

    private static void CheckCueFormatExport(AudioInput input, ReadOnlySpan<double> reference, int channels, string path, string format)
    {
        new FFmpegAudioConverter().Convert(input, path, format);
        using var exported = new PcmDecoder();
        Require(exported.Open(path, 88200, 0) && exported.Channels == channels, $"Cannot read the exported {format}.");
        CompareCueFormatSamples(exported, reference);
    }

    private static void CompareCueFormatSamples(PcmDecoder decoder, ReadOnlySpan<double> expected)
    {
        var buffer = new double[4093 * decoder.Channels];
        int position = 0;
        int frames;
        while ((frames = decoder.Read(buffer)) > 0)
        {
            int count = frames * decoder.Channels;
            Require(count <= expected.Length - position && buffer.AsSpan(0, count).SequenceEqual(expected.Slice(position, count)),
                $"PCM differs at frame {position / decoder.Channels}.");
            position += count;
        }
        Require(position == expected.Length, $"Decoded {position} samples, expected {expected.Length}.");
    }
}
