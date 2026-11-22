using AudioPlayer;
using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private const int CueRate = 44100;

    private static void RunCuePlaybackTests(string? sampleAlbumPath)
    {
        var fixture = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, $"cue-playback-{Guid.NewGuid():N}"));
        try
        {
            string wav = Path.Combine(fixture.FullName, "音源 [PCM].wav");
            WriteCueWav(wav);
            Run("CUE PCM WAV: exact clips, clamped seek, replay and stale source rejection", () => CheckCueWav(wav));
            Run("CUE IPC: legacy coexistence, range validation, gapless rendering and recovery", () => CheckCueIpcAndGapless(wav));
            Run("CUE fade: latest same-path play replaces an in-flight fade and prepares paused", () => CheckCueFadeReplacement(wav));
            Run("CUE loudness: each sample interval owns a distinct measurement and cache entry", () => CheckCueLoudness(wav, fixture.FullName));
            if (sampleAlbumPath is not null)
                Run("CUE FLAC: real album clip and seek boundaries against a sequential decode", () => CheckCueFlac(sampleAlbumPath));
        }
        finally { fixture.Delete(true); }
    }

    private static void CheckCueWav(string path)
    {
        var range = new AudioSampleRange(3171, 14798, CueRate);
        using var decoder = new PcmDecoder();
        Require(decoder.Open(path, 88200, 0, sampleRange: range), "Could not open WAV range");
        CheckCueWavFrames(ReadCueToEnd(decoder), range.StartSample, (int)range.FrameCount);
        Require(decoder.TotalMs == range.DurationMs && decoder.SeekToMs(101), "Track-relative seek failed");
        long target = range.SampleAt(101);
        CheckCueWavFrames(ReadCueToEnd(decoder), target, (int)(range.EndSample - target));
        Require(decoder.SeekToMs(long.MaxValue) && decoder.Read(new double[512]) == 0, "Seek beyond end read source samples");
        Require(decoder.SeekToMs(-100), "Rewind after EOF failed");
        CheckCueWavFrames(ReadCueToEnd(decoder), range.StartSample, (int)range.FrameCount);

        using var wrongRate = new PcmDecoder();
        using var wrongEnd = new PcmDecoder();
        Require(!wrongRate.Open(path, 88200, 0, sampleRange: range with { SampleRate = 48000 }), "Stale source rate accepted");
        Require(!wrongEnd.Open(path, 88200, 0, sampleRange: range with { EndSample = CueRate * 5L }), "Stale source length accepted");

        using var resampled = new PcmDecoder();
        Require(resampled.Open(path, 88200, 0, forceRate: 48000, sampleRange: range), "Could not open resampled WAV range");
        long expectedResampledFrames = (range.FrameCount * 48000 + CueRate - 1) / CueRate;
        Require(ReadCueToEnd(resampled).Length == expectedResampledFrames * 2, "Range did not drain its resampler to the expected length");

        var tempoRange = new AudioSampleRange(CueRate * 2L - 13003, CueRate * 2L, CueRate);
        using var accelerated = new PcmDecoder();
        Require(accelerated.Open(path, 88200, 0, sampleRange: tempoRange), "Could not open WAV range for variable speed");
        var tempo = new TempoProcessor(CueRate, 2, 1.5);
        var buffer = new double[2456];
        long rendered = 0;
        int frames;
        while ((frames = tempo.Read(accelerated, buffer)) > 0)
        {
            rendered += frames;
            for (int i = 0; i < frames * 2; i++)
                Require(Math.Abs(buffer[i]) <= 1000.0 / 32768, "Faster playback consumed the adjacent loud track");
        }
        Require(rendered == (long)Math.Round(tempoRange.FrameCount / 1.5), "Faster playback did not drain at the clip boundary");
    }

    private static void CheckCueIpcAndGapless(string path)
    {
        var firstInput = new AudioInput(path, new AudioSampleRange(311, 8619, CueRate));
        var nextInput = new AudioInput(path, new AudioSampleRange(8619, 17461, CueRate));
        using var ipc = new PlayerIpcService();
        var engine = Engine("WasapiShared");
        Set(engine, "_streamLock", new object());
        Set(engine, "_dspSettings", new DspSettings { IsEnabled = false, GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade });
        Set(ipc, "_engine", engine);
        Set(engine, "_ipc", ipc);

        byte[] occurrence = AudioInputProtocol.WriteOccurrence(317, firstInput);
        Require(AudioInputProtocol.ReadOccurrence(occurrence) == (317, firstInput), "Playback identity or Unicode path changed in IPC");
        Require(ipc.HandleCommand(CommandId.PlayAudioInput, occurrence[..9]).Type == MessageTypeId.Failed, "Truncated range was accepted");
        Require(ipc.HandleCommand(CommandId.PlayOccurrence, occurrence).Type == MessageTypeId.Failed, "Range was accepted by a legacy playback command");
        Require(ipc.HandleCommand(CommandId.SetAudioInput, AudioInputProtocol.Write(firstInput)).Type == MessageTypeId.Success, "Failed to prepare saved track");
        engine.ChangingSetting();
        var first = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
        Require(!engine.IsPlaying && first.TotalMs == firstInput.SampleRange!.Value.DurationMs, "Startup prepared the full image");

        var source = (GaplessSource)Invoke(engine, "OutputSource", first)!;
        engine.IsPlaying = true;
        var request = new GaplessRequest(first.TimelineEpoch, 321, nextInput.Path, true, nextInput.SampleRange);
        Require(GaplessRequest.ReadRanged(request.WriteRanged()) == request, "Range plan changed in IPC");
        Require(ipc.HandleCommand(CommandId.QueueNextAudioInput, request.WriteRanged()).Type == MessageTypeId.Success, "Range preload rejected");
        Require(SpinWait.SpinUntil(() => first.PcmInputStatus.Ended && source.Pending?.PcmInputStatus.Ended == true, 5000), "Both clips failed to prebuffer");
        var incoming = source.Pending!;
        var otherRange = request with { SampleRange = firstInput.SampleRange };
        var rejected = ipc.HandleCommand(CommandId.TransitionNowAudioInput, otherRange.WriteRanged());
        Require(rejected.Type == MessageTypeId.Success && rejected.Payload.Span[0] == (byte)TransitionNowStatus.Unavailable,
            "Same-path transition accepted a different range under the same token");
        int frameCount = (int)(nextInput.SampleRange!.Value.EndSample - firstInput.SampleRange!.Value.StartSample);
        int tail = 13;
        var pcm = new double[(frameCount + tail) * 2];
        source.FillPcm(pcm, frameCount + tail);
        CheckCueWavFrames(pcm.AsSpan(0, frameCount * 2), firstInput.SampleRange.Value.StartSample, frameCount);
        Require(pcm.AsSpan(frameCount * 2).IndexOfAnyExcept(0.0) < 0, "The physical source leaked past the second track");
        engine.SynchronizeGapless();
        Require(source.Current == incoming && GetTestInput(engine) == nextInput && source.CompletedToken == request.Token,
            "Gapless commit lost the ranged playback identity");

        engine.IsPlaying = false;
        engine.ChangeWaveChannelTime(70, seekId: 123);
        Require(engine.TryCaptureProgress(out var snapshot) && snapshot.CurrentMs == 70 && snapshot.TotalMs == nextInput.SampleRange.Value.DurationMs
            && snapshot.PlaybackId == request.Token,
            "Gapless seek reported a physical image position");
        Span<byte> progressPayload = stackalloc byte[ProgressProtocol.WithPlaybackIdSize];
        int progressBytes = ProgressProtocol.Write(progressPayload, snapshot);
        Require(progressBytes == ProgressProtocol.WithPlaybackIdSize && ProgressProtocol.Read(progressPayload) == snapshot,
            "Ranged progress lost its playback occurrence across serialization");
        engine.ChangingSetting();
        var rebuilt = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
        Require(!ReferenceEquals(rebuilt, incoming) && rebuilt.CurrentMs == 70, "Device setting change did not preserve the track-relative anchor");
        Require(SpinWait.SpinUntil(() => rebuilt.PcmInputStatus.Ended, 5000), "Rebuilt decoder did not resume");
        var afterRebuild = new double[256];
        rebuilt.FillPcm(afterRebuild, 128);
        CheckCueWavFrames(afterRebuild, nextInput.SampleRange.Value.SampleAt(70), 128);

        engine.MusicEnd();
        Require(SpinWait.SpinUntil(() => rebuilt.PcmInputStatus.Ended, 5000), "Stop/replay did not rewind the range");
        rebuilt.FillPcm(afterRebuild, 128);
        CheckCueWavFrames(afterRebuild, nextInput.SampleRange.Value.StartSample, 128);
        engine.ChangeWaveChannelTime(long.MaxValue, seekId: 124);
        Require(engine.TryCaptureProgress(out snapshot) && snapshot.CurrentMs == snapshot.TotalMs && snapshot.SeekId == 124,
            "End seek did not clamp the track-relative progress");
    }

    private static void CheckCueFadeReplacement(string path)
    {
        using var ipc = new PlayerIpcService();
        var engine = Engine("WasapiShared");
        var controlGate = new object();
        Set(engine, "_streamLock", controlGate);
        Set(ipc, "_engine", engine);
        Set(engine, "_ipc", ipc);
        var initial = new AudioInput(path, new AudioSampleRange(0, CueRate * 4L, CueRate));
        var replaced = new AudioInput(path, new AudioSampleRange(3171, 14798, CueRate));
        var latest = new AudioInput(path, new AudioSampleRange(611, 21296, CueRate));
        engine.SetAudioInput(initial);
        engine.ChangingSetting();
        var first = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
        Set(engine, "_output", new CorrectionOutput("cue-fade"));
        engine.IsFadingEnabled = engine.IsPlaying = true;
        var fadeField = typeof(PlaybackEngine).GetField("_fadeBusy", Private)!;
        lock (controlGate)
        {
            Require(ipc.HandleCommand(CommandId.PlayAudioInput, AudioInputProtocol.WriteOccurrence(501, replaced)).Type == MessageTypeId.Success
                && ipc.HandleCommand(CommandId.PlayAudioInput, AudioInputProtocol.WriteOccurrence(502, latest)).Type == MessageTypeId.Success,
                "Play requests were rejected");
            Require(GetTestInput(engine) == initial && (int)fadeField.GetValue(engine)! == 1,
                "In-flight fade overwrote the active decoder input");
            engine.PlayButton();
        }
        Require(SpinWait.SpinUntil(() => (int)fadeField.GetValue(engine)! == 0, 3000), "Fade did not settle");
        var paused = (Session)typeof(PlaybackEngine).GetField("_session", Private)!.GetValue(engine)!;
        Require(!engine.IsPlaying && paused != first && GetTestInput(engine) == latest,
            "Pause did not leave the latest requested clip ready");
        Require(SpinWait.SpinUntil(() => paused.PcmInputStatus.Ended, 5000), "Latest clip did not finish prebuffering");
        paused.Gain!.SetImmediately(1);
        var rendered = new double[256];
        paused.FillPcm(rendered, 128);
        CheckCueWavFrames(rendered, latest.SampleRange!.Value.StartSample, 128);
    }

    private static void CheckCueLoudness(string path, string cache)
    {
        var quiet = new AudioSampleRange(0, CueRate * 2L, CueRate);
        var loud = new AudioSampleRange(quiet.EndSample, CueRate * 4L, CueRate);
        var a = LoudnessScanner.ScanAsync(path, CueRate, 2, 88200, 0, default, cache, sampleRange: quiet).GetAwaiter().GetResult();
        var b = LoudnessScanner.ScanAsync(path, CueRate, 2, 88200, 0, default, cache, sampleRange: loud).GetAwaiter().GetResult();
        Require(a != null && b != null && b.IntegratedLufs - a.IntegratedLufs > 15 && b.SamplePeak > a.SamplePeak * 6,
            "Adjacent ranges shared the same loudness measurement");
        int entries = Directory.GetFiles(cache, "*.bin").Length;
        Require(entries == 2, $"Expected two range-keyed loudness cache entries, found {entries}");
    }

    private static void CheckCueFlac(string path)
    {
        const int tail = 8191;
        const int prefix = 10003;
        long[] boundaries = [11_186_112, 20_996_304, 29_364_720, 40_085_136, 49_006_272, 61_243_728, 72_074_688, 84_443_856, 94_518_159];
        using var continuous = new PcmDecoder();
        Require(continuous.Open(path, 88200, 0), "Could not open the reference FLAC");
        var scratch = new double[8192];
        long position = 0;
        foreach (long boundary in boundaries)
        {
            bool physicalEnd = boundary == boundaries[^1];
            var range = new AudioSampleRange(boundary - prefix, physicalEnd ? boundary : boundary + tail, CueRate);
            var reference = new List<double>();
            while (position < range.EndSample)
            {
                int count = continuous.Read(scratch);
                Require(count > 0, "Reference FLAC ended too soon");
                int start = (int)Math.Max(0, range.StartSample - position);
                int end = (int)Math.Min(count, range.EndSample - position);
                if (start < end) reference.AddRange(scratch.AsSpan(start * 2, (end - start) * 2).ToArray());
                position += count;
            }

            using var outgoing = new PcmDecoder();
            Require(outgoing.Open(path, 88200, 0, sampleRange: range with { EndSample = boundary }), $"Could not open the outgoing FLAC clip at {boundary}");
            if (physicalEnd)
            {
                Require(ReadCueToEnd(outgoing).SequenceEqual(reference) && continuous.Read(scratch) == 0,
                    "Final clip changed or skipped a sample at the physical FLAC end");
                continue;
            }
            using var incoming = new PcmDecoder();
            Require(incoming.Open(path, 88200, 0, sampleRange: range with { StartSample = boundary }), $"Could not open the incoming FLAC clip at {boundary}");
            Require(ReadCueToEnd(outgoing).Concat(ReadCueToEnd(incoming)).SequenceEqual(reference),
                $"A sample was skipped, duplicated or changed at FLAC boundary {boundary}");
            Require(incoming.SeekToMs(51), "Real FLAC seek failed");
            int startIndex = prefix + 51 * CueRate / 1000;
            Require(ReadCueToEnd(incoming).SequenceEqual(reference.Skip(startIndex * 2)), "Real FLAC seek landed on the wrong source sample");
        }
    }

    private static double[] ReadCueToEnd(PcmDecoder decoder)
    {
        var result = new List<double>();
        var block = new double[263 * 2];
        int frames;
        while ((frames = decoder.Read(block)) > 0)
            result.AddRange(block.AsSpan(0, frames * 2).ToArray());
        return result.ToArray();
    }

    private static void WriteCueWav(string path)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int frames = CueRate * 4;
        writer.Write("RIFF"u8);
        writer.Write(36 + frames * 4);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)2);
        writer.Write(CueRate);
        writer.Write(CueRate * 4);
        writer.Write((short)4);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(frames * 4);
        for (int sample = 0; sample < frames; sample++)
        {
            writer.Write(CueWavSample(sample, 0));
            writer.Write(CueWavSample(sample, 1));
        }
    }

    private static short CueWavSample(long sample, int channel)
    {
        int amplitude = sample < CueRate * 2L ? 1000 : 14000;
        return (short)(Math.Sin(sample * (channel == 0 ? 0.353 : 0.19)) * amplitude);
    }

    private static void CheckCueWavFrames(ReadOnlySpan<double> pcm, long start, int frames)
    {
        Require(pcm.Length == frames * 2, $"Expected {frames} frames, decoded {pcm.Length / 2}");
        for (int i = 0; i < frames; i++)
            if (pcm[2 * i] != CueWavSample(start + i, 0) / 32768.0 || pcm[2 * i + 1] != CueWavSample(start + i, 1) / 32768.0)
                throw new InvalidOperationException($"Wrong PCM at source sample {start + i}.");
    }
}
