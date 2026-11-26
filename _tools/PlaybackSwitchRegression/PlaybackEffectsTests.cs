using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunPlaybackEffectsTests()
    {
        RunGaplessLifecycleTests();
        RunTrackTransitionTests();
        Run("Export: failed native resampler initialization releases ownership", () =>
        {
            FFmpeg.AutoGen.AVChannelLayout stereo = default;
            FFmpeg.AutoGen.ffmpeg.av_channel_layout_default(&stereo, 2);
            FFmpeg.AutoGen.SwrContext* context = null;
            try
            {
                Require(FFmpeg.AutoGen.ffmpeg.swr_alloc_set_opts2(&context, &stereo,
                    FFmpeg.AutoGen.AVSampleFormat.AV_SAMPLE_FMT_FLT, 48000, &stereo,
                    FFmpeg.AutoGen.AVSampleFormat.AV_SAMPLE_FMT_FLT, 0, 0, null) >= 0 && context != null,
                    "Native setup did not allocate");
                try
                {
                    WinUIMusicPlayer.AudioConverters.FFmpegAudioConverter.InitializeOwnedResampler(ref context);
                    throw new Exception("Invalid input rate initialized");
                }
                catch (InvalidOperationException) { Require(context == null, "Failed resampler retained native ownership"); }
            }
            finally
            {
                FFmpeg.AutoGen.ffmpeg.swr_free(&context);
                FFmpeg.AutoGen.ffmpeg.av_channel_layout_uninit(&stereo);
            }
        });
        Run("Dynamics: linked compression lifts quiet audio, reduces loud audio and limits transients", () =>
        {
            var settings = new DspSettings { CompressorEnabled = true };
            double Measure(double amplitude)
            {
                var processor = new DynamicsProcessor(48000, 2);
                double[] audio = new double[48000 * 2];
                for (int i = 0; i < audio.Length; i += 2) { audio[i] = amplitude; audio[i + 1] = -amplitude / 2; }
                processor.Process(audio, 48000, settings);
                Require(audio.All(s => double.IsFinite(s) && Math.Abs(s) <= Math.Pow(10, -1.0 / 20) + 1e-14), "Ceiling exceeded");
                Require(Math.Abs(audio[^2] / audio[^1] + 2) < 1e-12, "Channel image changed");
                return audio[^2];
            }
            Require(Measure(0.02) > 0.035, "Quiet passage not lifted");
            Require(Measure(0.8) < 0.5, "Loud passage not compressed");
            var limiter = new DynamicsProcessor(48000, 6);
            double[] impulse = [10, -5, 0.2, double.NaN, double.PositiveInfinity, 0];
            limiter.Process(impulse, 1, settings with { CompressorMakeupDb = 18 });
            Require(impulse.All(x => double.IsFinite(x) && Math.Abs(x) <= 0.891251), "Invalid impulse output");
        });
        Run("Dynamics: compressor-only processing follows the shared peak ceiling", () =>
        {
            foreach (double ceiling in new[] { -12.0, -6, -1, -0.1 })
            {
                var processor = new DynamicsProcessor(48000, 2);
                var settings = new DspSettings { CompressorEnabled = true, CompressorRatio = 1,
                    CompressorMakeupDb = 18, TruePeakCeilingDb = ceiling };
                double[] audio = [1.5, -0.75, 1.5, -0.75];
                processor.Process(audio, 2, settings);
                double expected = Math.Pow(10, ceiling / 20);
                Require(Math.Abs(audio[0] - expected) < 1e-12 && audio[1] == -audio[0] / 2,
                    "Compressor retained a separate fixed ceiling or changed the channel ratio");
                audio[0] = 1.5; audio[1] = -0.75;
                processor.Process(audio, 1, settings with { TruePeakCeilingDb = -12 });
                Require(audio[0] <= Math.Pow(10, -12.0 / 20) + 1e-12, "Lowered ceiling was not applied immediately");
            }
        });
        Run("Dynamics: bypass is bit-exact and active processing has no managed block allocations", () =>
        {
            var processor = new DynamicsProcessor(48000, 2);
            double[] audio = [0.25, -0.1, 0, -0.0];
            byte[] original = System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan()).ToArray();
            processor.Process(audio, 2, new());
            Require(original.AsSpan().SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(audio.AsSpan())), "Bypass altered samples");
            var settings = new DspSettings { CompressorEnabled = true };
            processor.Process(audio, 2, settings);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) processor.Process(audio, 2, settings);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Render allocated");
        });
        Run("Playback settings: v6 roundtrip, legacy defaults and invalid-value normalization", () =>
        {
            var settings = new DspSettings { GaplessPlayback = false, PlaybackRate = 1.25, CompressorEnabled = true,
                CompressorThresholdDb = -24, CompressorRatio = 3, CompressorMakeupDb = 9 };
            byte[] bytes = new byte[DspProtocol.SettingsSize];
            DspProtocol.WriteSettings(bytes, settings);
            Require(DspProtocol.ReadSettings(bytes) == settings, "v6 roundtrip");
            bytes[0] = 5;
            var legacy = DspProtocol.ReadSettings(bytes.AsSpan(0, 1596));
            Require(legacy.PlaybackRate == 1 && !legacy.CompressorEnabled && legacy.GaplessPlayback, "v5 defaults");
            var invalid = new DspSettings { PlaybackRate = double.NaN, CompressorThresholdDb = -100,
                CompressorRatio = double.PositiveInfinity, CompressorMakeupDb = 50 }.Sanitize();
            Require(invalid.PlaybackRate == 1 && invalid.CompressorThresholdDb == -40
                && invalid.CompressorRatio == 4 && invalid.CompressorMakeupDb == 18, "Unsafe values survived");
            var snapshot = new ProgressSnapshot(1, 2, 300, 1000, 50, true, 0, 1.5, 73);
            byte[] progress = new byte[ProgressProtocol.Size];
            ProgressProtocol.Write(progress, snapshot);
            Require(ProgressProtocol.Read(progress) == snapshot, "Rate/token telemetry lost");
            foreach (double rate in new[] { 0.25, 5.0 })
            {
                var endpoint = settings with { PlaybackRate = rate };
                Require(endpoint.Sanitize().PlaybackRate == rate, "Valid endpoint clamped");
                DspProtocol.WriteSettings(bytes, endpoint);
                Require(DspProtocol.ReadSettings(bytes) == endpoint, "Endpoint settings lost");
                var endpointProgress = snapshot with { PlaybackRate = rate };
                ProgressProtocol.Write(progress, endpointProgress);
                Require(ProgressProtocol.Read(progress) == endpointProgress, "Endpoint telemetry lost");
            }
            Require((settings with { PlaybackRate = 0.1 }).Sanitize().PlaybackRate == 0.25, "Lower bound");
            Require((settings with { PlaybackRate = 6 }).Sanitize().PlaybackRate == 5, "Upper bound");
        });
        Run("Concert hall: reflections and decay follow sample rate and reset at seek", () =>
        {
            foreach (int rate in new[] { 44100, 96000 })
            {
                var settings = new DspSettings { ConcertHallEnabled = true };
                using var effects = new PcmEffects(rate, 2);
                effects.Configure(settings);
                var silence = new double[rate * 2];
                effects.ApplyInput(silence, rate);
                effects.ApplyConcertHall(silence, rate);
                double[] response = new double[rate * 7];
                response[0] = 1;
                response[1] = 1;
                effects.ApplyConcertHall(response, response.Length / 2);
                int firstLeft = (int)Math.Round(rate * 0.017) * 2;
                int firstRight = (int)Math.Round(rate * 0.023) * 2 + 1;
                Require(response.AsSpan(2, firstLeft - 2).IndexOfAnyExcept(0) < 0 &&
                    Math.Abs(response[0] - 0.82) < 1e-14 && Math.Abs(response[1] - 0.82) < 1e-14 &&
                    response[firstLeft] > 0.18 && response[firstLeft + 1] == 0 && response[firstRight] > 0.17,
                    "Stereo early reflections did not follow sample rate");

                static double Peak(Span<double> window)
                {
                    double peak = 0;
                    foreach (double value in window) peak = Math.Max(peak, Math.Abs(value));
                    return peak;
                }

                double depth = Peak(response.AsSpan(3 * rate / 2, 3 * rate / 2));
                double late = Peak(response.AsSpan(5 * rate, 3 * rate / 2));
                Require(depth > 4e-4 && late < depth * 0.02 && effects.TailFrames == 13 * rate / 4,
                    "Hall did not sustain and decay within the extended tail budget");

                using var mono = new PcmEffects(rate, 1);
                mono.Configure(settings);
                var monoSilence = new double[rate];
                mono.ApplyInput(monoSilence, rate);
                mono.ApplyConcertHall(monoSilence, rate);
                var monoResponse = new double[response.Length / 2];
                monoResponse[0] = 1;
                for (int start = 0; start < monoResponse.Length; start += 257)
                {
                    var block = monoResponse.AsSpan(start, Math.Min(257, monoResponse.Length - start));
                    mono.ApplyConcertHall(block, block.Length);
                }
                for (int frame = 0; frame < monoResponse.Length; frame++)
                    Require(Math.Abs(monoResponse[frame] - 0.5 * (response[2 * frame] + response[2 * frame + 1])) < 1e-14,
                        "Mono fold-down or rendering across blocks changed");
                effects.RequestReset();
                effects.ApplyInput(silence, rate);
                effects.ApplyConcertHall(silence, rate);
                Require(silence.AsSpan().IndexOfAnyExcept(0) < 0, "Seek leaked old history");
            }
            using var surround = new PcmEffects(48000, 6);
            surround.Configure(new() { ConcertHallEnabled = true });
            double[] samples = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6];
            surround.ApplyInput(samples, 1);
            surround.ApplyConcertHall(samples, 1);
            Require(samples.AsSpan().SequenceEqual([0.1, -0.2, 0.3, -0.4, 0.5, -0.6]) && surround.TailFrames == 0,
                "Concert hall applied to an unsupported multichannel layout");
        });
        Run("Concert hall: hot mono, stereo and widened preset preserve PCM headroom", () =>
        {
            const int rate = 48000;
            foreach ((int channels, bool preset) in new[] { (1, false), (2, false), (2, true) })
            {
                using var effects = new PcmEffects(rate, channels);
                effects.Configure(new() { ConcertHallEnabled = true, StereoWidth = preset ? 1.3 : 1 });
                var eq = new Equalizer();
                if (preset) eq.Configure(rate, true, [-2, -1, 0, 0, -0.5f, 0, 0, -0.5f, -1.5f, -2.5f]);
                const int frameCount = 2 * rate;
                var samples = new double[frameCount * channels];
                for (int frame = 0; frame < frameCount; frame++)
                {
                    double sample = Math.Sin(2 * Math.PI * 440 * frame / rate);
                    for (int channel = 0; channel < channels; channel++)
                        samples[frame * channels + channel] = sample;
                }
                effects.ApplyInput(samples, frameCount);
                eq.Process(samples, frameCount, channels);
                effects.ApplyConcertHall(samples, frameCount);
                effects.ApplyStereo(samples, frameCount);
                effects.ApplyFinal(samples, frameCount);
                Require(effects.DelayFrames == 0 && samples.All(sample => Math.Abs(sample) <= 1 + 1e-12),
                    "Hall exceeded PCM headroom with the independent limiter off");

                effects.Configure(new DspSettings());
                Array.Clear(samples);
                effects.ApplyInput(samples, frameCount);
                effects.ApplyConcertHall(samples, frameCount);
                Require(samples.AsSpan(rate * channels / 20).IndexOfAnyExcept(0) < 0 && effects.TailFrames == 0,
                    "Disabled hall continued rendering after the gain ramp");

                effects.Configure(new() { ConcertHallEnabled = true });
                Array.Clear(samples);
                effects.ApplyInput(samples, frameCount);
                effects.ApplyConcertHall(samples, frameCount);
                Require(samples.AsSpan().IndexOfAnyExcept(0) < 0, "Re-enabled hall leaked old history");
            }
        });
        Run("Concert hall settings: v10 round-trip, v9 compatibility and invalid flag rejection", () =>
        {
            var settings = new DspSettings { ConcertHallEnabled = true, CrossfadeMilliseconds = 1250 };
            var preview = new DspPreview(17, 3, "device", false, settings);
            var payload = new byte[DspPreview.Size];
            preview.Write(payload);
            Require(DspPreview.Read(payload) == preview, "Concert hall was lost in v10 preview");

            payload[0] = 9;
            int sizeV9 = DspProtocol.SettingsSize - 1;
            payload.AsSpan(DspProtocol.SettingsSize).CopyTo(payload.AsSpan(sizeV9));
            Require(DspPreview.Read(payload.AsSpan(0, payload.Length - 1)) ==
                preview with { Settings = settings with { ConcertHallEnabled = false } }, "v9 preview changed");
            preview.Write(payload);
            payload[sizeV9] = 2;
            try
            {
                DspPreview.Read(payload);
                throw new Exception("Invalid concert hall flag accepted");
            }
            catch (ArgumentException) { }
        });
        Run("Gapless: splice inside a callback, with exact sample order and continuous submitted frames", () =>
        {
            var engine = Engine("WasapiShared");
            using var first = BufferedSource(engine, [0.1, -0.1, 0.2, -0.2, 0.3, -0.3]);
            using var second = BufferedSource(engine, [0.4, -0.4, 0.5, -0.5, 0.6, -0.6]);
            var source = new GaplessSource(first);
            Require(source.Queue(second, 19), "Queue rejected");
            double[] output = new double[10];
            source.FillPcm(output, 5);
            Require(output.AsSpan().SequenceEqual(new double[] { 0.1, -0.1, 0.2, -0.2, 0.3, -0.3, 0.4, -0.4, 0.5, -0.5 }), "Gap, fade or dropped frame at boundary");
            Require(source.Current == second && source.CompletedToken == 19 && source.SubmittedFrames == 5, "Transition identity/counter incorrect");
            source.FillPcm(output, 5);
            Require(output[0] == 0.6 && output[1] == -0.6 && output.AsSpan(2).ToArray().All(s => s == 0), "Tail not drained correctly");
            Require(source.SubmittedFrames == 6, "EOF silence counted as audio");
        });
        Run("Gapless: cancellation and incompatible format never consume the next source", () =>
        {
            var engine = Engine("WasapiShared");
            using var first = BufferedSource(engine, [0.1, 0.1]);
            using var second = BufferedSource(engine, [0.2, 0.2]);
            using var mismatch = Source(engine, RenderKind.Pcm, 96000);
            var source = new GaplessSource(first);
            Require(!source.Queue(mismatch, 1), "Mismatched format accepted");
            Require(source.Queue(second, 2) && source.Cancel() == second, "Cancel did not return ownership");
            source.FillPcm(new double[8], 4);
            Require(source.Current == first && second.SubmittedFrames == 0 && source.CompletedToken == 0, "Cancelled track became audible");
        });
        Run("Gapless: EQ, convolution, concert hall, stereo and compressor histories match uninterrupted processing", () =>
        {
            string impulse = Path.Combine(AppContext.BaseDirectory, "gapless-impulse.wav");
            double[] taps = new double[512];
            taps[0] = 0.8; taps[17] = 0.1; taps[300] = 0.1;
            WriteIr(impulse, taps);
            var settings = new DspSettings { CompressorEnabled = true, ConcertHallEnabled = true, Crossfeed = 0.2, StereoWidth = 1.2,
                ConvolutionEnabled = true, ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = impulse,
                AutoPreamp = false, ConvolutionTrimDb = 0 };
            var engine = Engine("WasapiShared");
            var random = new Random(73);
            double[] dry = Enumerable.Range(0, 8192).Select(_ => (random.NextDouble() - 0.5) * 1.6).ToArray();
            const int split = 1377;
            using var continuous = BufferedSource(engine, dry);
            using var first = BufferedSource(engine, dry[..(split * 2)]);
            using var second = BufferedSource(engine, dry[(split * 2)..]);
            foreach (var session in new[] { continuous, first, second })
            {
                session.ConfigureDsp(settings);
                float[] gains = new float[10]; gains[5] = 3;
                session.Eq.Configure(48000, true, gains);
                Require(SpinWait.SpinUntil(() => session.Effects!.GetState(0, true).Convolution == ConvolutionStatus.Active, 3000), "IR did not load");
            }
            double[] expected = new double[dry.Length], actual = new double[dry.Length];
            continuous.FillPcm(expected, 4096);
            var source = new GaplessSource(first);
            Require(source.Queue(second, 41), "Queue rejected");
            source.FillPcm(actual, 4096);
            for (int i = 0; i < actual.Length; i++)
                Require(Math.Abs(actual[i] - expected[i]) < 1e-12, $"Effect discontinuity at sample {i}: {actual[i]} / {expected[i]}");
        });
        Run("Tempo: real decoder duration, preserved pitch, stereo phase and seek reset", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "tempo-reference.wav");
            WriteLoudnessTone(path, 0.1f); // 3 seconds, 48 kHz, 1 kHz stereo
            foreach (double speed in new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 5 })
            {
                using var decoder = new PcmDecoder();
                Require(decoder.Open(path, 88200, 0), "Decoder open failed");
                var tempo = new TempoProcessor(48000, 2, speed);
                var audio = new List<double>();
                double[] buffer = new double[2048];
                int frames;
                while ((frames = tempo.Read(decoder, buffer)) > 0) audio.AddRange(buffer.AsSpan(0, frames * 2).ToArray());
                Require(audio.Count / 2 == (int)Math.Round(144000 / speed), $"Wrong duration at {speed}: {audio.Count / 2}");
                int crossings = 0;
                for (int frame = 1; frame < audio.Count / 2; frame++)
                {
                    Require(audio[frame * 2] == audio[frame * 2 + 1], "Stereo phase mismatch");
                    if (audio[(frame - 1) * 2] < 0 && audio[frame * 2] >= 0) crossings++;
                }
                double frequency = crossings * 48000.0 / (audio.Count / 2);
                Require(Math.Abs(frequency - 1000) < 8, $"Pitch shifted at {speed}: {frequency}");
                Require(decoder.SeekToMs(0), "Seek failed");
                tempo.Reset();
                frames = tempo.Read(decoder, buffer);
                Require(audio.Take(frames * 2).SequenceEqual(buffer.AsSpan(0, frames * 2).ToArray()), "Seek retained tempo history");
            }
        });
        Run("Gapless: asynchronous engine preload, stale epoch, adoption and stop cleanup", () =>
        {
            string path = Path.Combine(AppContext.BaseDirectory, "gapless-next.wav");
            WriteLoudnessTone(path, 0.1f);
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            Set(engine, "_dspSettings", new DspSettings());
            var first = BufferedSource(engine, [0.1, 0.1]);
            Set(engine, "_session", first);
            var source = new GaplessSource(first);
            Set(engine, "_gaplessSource", source);
            engine.IsPlaying = true;
            engine.QueueNext(new(first.TimelineEpoch + 1, 10, path));
            Require(source.Pending == null, "Stale epoch accepted");
            var missing = new GaplessRequest(first.TimelineEpoch, 10, path + ".missing");
            engine.QueueNext(missing);
            var preloader = (GaplessPreloader)typeof(PlaybackEngine).GetField("_gaplessPreloader", Private)!.GetValue(engine)!;
            var failedWork = preloader.Completion;
            failedWork.GetAwaiter().GetResult();
            engine.QueueNext(missing);
            Require(ReferenceEquals(failedWork, preloader.Completion),
                "Unchanged failed plan repeatedly reopened the file");
            engine.QueueNext(new(first.TimelineEpoch, 11, path));
            Require(SpinWait.SpinUntil(() => source.Pending is { ReadyFrames: > 4800 }, 5000), "Next did not preload");
            var prepared = source.Pending;
            engine.UpdateDsp(new DspSettings { Balance = 0.2, CompressorEnabled = true, AutoPreamp = false });
            Require(ReferenceEquals(source.Pending, prepared), "Ordinary DSP update discarded the prepared session");
            source.FillPcm(new double[512], 256);
            engine.SynchronizeGapless();
            Require(GetTestInput(engine).Path == path && source.CompletedToken == 11, "Engine did not adopt next track");
            Require(engine.TryCaptureProgress(out var snapshot) && snapshot.GaplessToken == 11, "Transition missing from telemetry");
            engine.MusicEnd();
            Require(source.Pending == null && !engine.IsPlaying, "Stop retained queued audio");
            Invoke(engine, "DisposeSession");
        });
    }

    private static Session BufferedSource(PlaybackEngine engine, double[] samples)
    {
        var session = Source(engine, RenderKind.Pcm, 48000);
        var ring = new PcmRing(2, Math.Max(1, samples.Length / 2), 0, 0);
        Set(session, "_pcmRing", ring);
        session.Gain!.SetImmediately(1);
        ring.Push(samples, samples.Length / 2, static () => false);
        ring.MarkInputEnded();
        return session;
    }
}
