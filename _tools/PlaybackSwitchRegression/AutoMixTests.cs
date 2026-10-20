using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunAutoMixTests()
    {
        Run("AutoMix: legacy preferences and IPC preserve all four transition modes", () =>
        {
            Require(new AudioPreferences().EffectiveTrackTransition == TrackTransitionMode.Gapless, "Default changed");
            Require(new AudioPreferences { IsFadeEnabled = true }.EffectiveTrackTransition == TrackTransitionMode.Crossfade, "Fade migration");
            Require(new AudioPreferences { Dsp = new() { GaplessPlayback = false } }.EffectiveTrackTransition == TrackTransitionMode.Normal, "Off migration");
            byte[] buffer = new byte[BinarySerializer.IpcSettingSize];
            foreach (var mode in Enum.GetValues<TrackTransitionMode>())
            {
                var original = new IpcSetting { TrackTransition = mode };
                int length = BinarySerializer.WriteIpcSetting(buffer, original);
                Require(BinarySerializer.ReadIpcSetting(buffer.AsSpan(0, length)).TrackTransition == mode, "Mode lost");
                Require(BinarySerializer.ReadIpcSetting(buffer.AsSpan(0, length - 1)).TrackTransition == null, "Legacy IPC changed");
            }
            var request = new GaplessRequest(3, 7, Path.GetFullPath("album.wav"), true);
            Require(GaplessRequest.Read(request.Write()) == request, "Album continuity lost");
            byte[] legacy = new byte[16 + System.Text.Encoding.UTF8.GetByteCount(request.Path)];
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(legacy, 3);
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(legacy.AsSpan(8), 7);
            System.Text.Encoding.UTF8.GetBytes(request.Path, legacy.AsSpan(16));
            Require(GaplessRequest.Read(legacy) == request with { PreserveContinuity = false }, "Legacy request changed");
        });
        Run("AutoMix: tempo clock is monotonic, invertible and returns smoothly to one", () =>
        {
            foreach (double rate in new[] { 0.94, 1.06 })
            {
                var clock = new TempoEnvelope(rate, 48000, 96000);
                for (int i = 0; i < 200000; i += 113)
                {
                    double source = clock.SourceFrames(i);
                    Require(Math.Abs(clock.OutputFrames(source) - i) < 0.0001, "Clock inversion drift");
                    Require(clock.SourceFrames(i + 1) > source, "Clock reversed");
                    Require(clock.RateAt(i) >= 0.94 && clock.RateAt(i) <= 1.06, "Rate exceeded envelope");
                }
                Require(clock.RateAt(144000) == 1 && clock.RateAt(200000) == 1, "Rate did not settle");
            }
        });
        Run("AutoMix: bounded energy analysis rejects silence and unreliable beats", () =>
        {
            float[] energy = new float[4800], onset = new float[4800];
            var silence = AutoMixAnalyzer.Measure(energy, onset, 0, 24, true);
            Require(!AutoMixPlanner.Plan(silence, silence, true).IsMix, "Silence created mix");
            var random = new Random(73);
            for (int i = 0; i < energy.Length; i++) energy[i] = (float)random.NextDouble();
            var noise = AutoMixAnalyzer.Measure(energy, onset, 0, 24, true);
            Require(noise.Confidence < 0.6, $"Noise accepted as a beat grid: {noise.Confidence}");
            var fade = AutoMixPlanner.Plan(noise, noise, true);
            Require(fade.IsMix && !fade.BeatAligned && fade.Rate == 1, "Unreliable beats did not crossfade");
            var empty = AutoMixPlanner.Plan(default, noise, true);
            Require(!empty.IsMix, "Unsupported source did not preserve gapless");
        });
        Run("AutoMix: overlap commits once, limits the summed peak and allocates zero managed render bytes", () =>
        {
            var engine = Engine("WasapiShared");
            double[] samples = new double[100000 * 2];
            for (int i = 0; i < samples.Length; i += 2) { samples[i] = 0.9; samples[i + 1] = -0.9; }
            using var first = BufferedSource(engine, samples);
            using var second = BufferedSource(engine, samples);
            Set(second, "<TransitionPlan>k__BackingField", new AutoMixPlan(0, 0, 1000, 1, 0, false));
            var source = new GaplessSource(first);
            double[] audio = new double[256];
            source.FillPcm(audio, 128); // warm FFT/buffer paths before measuring the complete transition
            Require(source.Queue(second, 41), "Mix queue rejected");
            long before = GC.GetAllocatedBytesForCurrentThread();
            double peak = 0;
            for (int block = 0; block < 400; block++)
            {
                source.FillPcm(audio, 128);
                for (int i = 0; i < audio.Length; i += 2)
                {
                    peak = Math.Max(peak, Math.Abs(audio[i]));
                    Require(audio[i] == -audio[i + 1] && double.IsFinite(audio[i]), "Stereo image/finite samples changed");
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"Render allocated {allocated} B");
            Require(peak <= 0.98000000001, $"Summed peak exceeded ceiling: {peak}");
            Require(source.Current == second && source.CompletedToken == 41 && source.Pending == null, "Wrong transition identity");
            Require(source.Outgoing == null && source.TakeRetired() == first && source.TakeRetired() == null, "Outgoing ownership lost");
            Console.WriteLine($"AutoMix render: {400 * 128} frames, {allocated} B allocated, peak={peak:0.000000}, scratch=8192 B");
        });
        Run("AutoMix: cancel before and after audible commit preserves ownership and progress", () =>
        {
            var engine = Engine("WasapiShared");
            double[] samples = new double[48000 * 2];
            Array.Fill(samples, 0.1);
            using var a = BufferedSource(engine, samples);
            using var b = BufferedSource(engine, samples);
            Set(b, "<TransitionPlan>k__BackingField", new AutoMixPlan(0, 0, 500, 1, 0, false));
            var source = new GaplessSource(a);
            Require(source.Queue(b, 1) && source.Cancel() == b && b.FramesPlayed == 0, "Pending cancel consumed incoming");
            Require(source.Queue(b, 2), "Requeue failed");
            double[] audio = new double[256];
            source.FillPcm(audio, 128);
            Require(source.Current == b && source.Outgoing == a && source.CompletedToken == 2, "Audible commit missing");
            Require(!source.Queue(a, 3), "Another plan interrupted the active overlap");
            long played = b.FramesPlayed;
            Require(source.Cancel() == null && source.Current == b && source.TakeRetired() == a, "Cancel rewound committed track");
            source.FillPcm(audio, 128);
            Require(b.FramesPlayed == played + 128 && source.CompletedToken == 2, "Cancel lost current progress");
        });
        Run("AutoMix: 192/384 kHz overlap keeps fixed scratch and zero callback allocations", () =>
        {
            foreach (int rate in new[] { 192000, 384000 })
            {
                var engine = Engine("WasapiShared");
                double[] samples = new double[rate * 2 * 2];
                Array.Fill(samples, 0.1);
                using var a = BufferedSource(engine, samples, rate);
                using var b = BufferedSource(engine, samples, rate);
                Set(b, "<TransitionPlan>k__BackingField", new AutoMixPlan(0, 0, 1000, 1, 0, false));
                var source = new GaplessSource(a);
                double[] audio = new double[1024];
                source.FillPcm(audio, 512);
                long setupStart = GC.GetAllocatedBytesForCurrentThread();
                Require(source.Queue(b, 3), "High-rate queue failed");
                long setup = GC.GetAllocatedBytesForCurrentThread() - setupStart;
                long before = GC.GetAllocatedBytesForCurrentThread(), started = System.Diagnostics.Stopwatch.GetTimestamp();
                for (int i = 0; i < rate; i += 512) source.FillPcm(audio, 512);
                double milliseconds = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
                Require(allocated == 0 && setup < 9000 && !source.IsTransitioning, "High rate grew scratch or allocated render blocks");
                Console.WriteLine($"AutoMix {rate} Hz: 1 s audio in {milliseconds:0.00} ms, callback={allocated} B, scratch setup={setup} B");
            }
        });

        string pathA = Path.Combine(AppContext.BaseDirectory, "automix-a.wav");
        string pathB = Path.Combine(AppContext.BaseDirectory, "automix-b.wav");
        WriteBeatFixture(pathA, 48000, 120, 40);
        WriteBeatFixture(pathB, 96000, 124, 40);
        Run("AutoMix: real 48/96 kHz decoder analysis aligns beats with a bounded tempo deviation", () =>
        {
            var a = AutoMixAnalyzer.Analyze(pathA, true, true, default);
            var b = AutoMixAnalyzer.Analyze(pathB, false, true, default);
            Console.WriteLine($"AutoMix analysis: A={a.Bpm:0.00}/{a.Confidence:0.000}, B={b.Bpm:0.00}/{b.Confidence:0.000}");
            Require(Math.Abs(a.Bpm - 120) < 0.5 && Math.Abs(b.Bpm - 124) < 0.5, "Wrong tempo");
            var plan = AutoMixPlanner.Plan(a, b, true);
            Require(plan.BeatAligned && Math.Abs(plan.Rate - 120.0 / 124) < 0.005, "No beat-matched plan");
            double phaseA = (plan.ExitMs / 1000.0 - a.Phase) * a.Bpm / 60;
            double phaseB = (plan.EntryMs / 1000.0 - b.Phase) * b.Bpm / 60;
            Require(Math.Abs(phaseA - Math.Round(phaseA)) < 0.002 && Math.Abs(phaseB - Math.Round(phaseB)) < 0.002, "Exit/entry off grid");
        });
        Run("AutoMix: real WSOLA returns to raw decoding, preserves duration/pitch and has no block allocations", () =>
        {
            string tonePath = Path.Combine(AppContext.BaseDirectory, "automix-tempo.wav");
            WriteLoudnessTone(tonePath, 0.1f);
            using var decoder = new PcmDecoder();
            Require(decoder.Open(tonePath, 88200, 0), "Open failed");
            var clock = new TempoEnvelope(1.04, 24000, 48000);
            var tempo = new TempoProcessor(48000, 2, 1, clock);
            double[] audio = new double[1024];
            int frames = tempo.Read(decoder, audio), total = frames, crossings = 0;
            double previous = audio[(frames - 1) * 2];
            long before = GC.GetAllocatedBytesForCurrentThread();
            while ((frames = tempo.Read(decoder, audio)) > 0)
            {
                total += frames;
                for (int i = 0; i < frames; i++)
                {
                    if (previous < 0 && audio[i * 2] >= 0) crossings++;
                    previous = audio[i * 2];
                    Require(audio[i * 2] == audio[i * 2 + 1], "Stereo phase changed");
                }
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(Math.Abs(total - clock.OutputFrames(144000)) <= 1, $"Duration drift: {total}");
            Require(tempo.CanDetach && allocated == 0, $"No raw bypass or block allocation: {allocated}");
            Require(Math.Abs(crossings * 48000.0 / (total - 512) - 1000) < 10, "Pitch changed");
            Console.WriteLine($"AutoMix WSOLA: {total} output frames, {allocated} B block allocation, raw bypass reached");
        });
        Run("AutoMix: compressed FLAC/MP3 seeks preserve the decoded beat phase", () =>
        {
            foreach (string format in new[] { "flac", "mp3" })
            {
                string path = Path.Combine(AppContext.BaseDirectory, $"automix-compressed.{format}");
                try
                {
                    new WinUIMusicPlayer.AudioConverters.FFmpegAudioConverter().Convert(pathA, path, format);
                    var reference = AutoMixAnalyzer.Analyze(pathA, true, true, default);
                    var compressed = AutoMixAnalyzer.Analyze(path, true, true, default);
                    double beat = 60 / reference.Bpm;
                    double difference = Math.Abs(reference.Phase - compressed.Phase) % beat;
                    difference = Math.Min(difference, beat - difference);
                    Require(compressed.Supported && Math.Abs(compressed.Bpm - reference.Bpm) < 0.5 && difference < 0.015,
                        $"{format}: phase drift {difference:0.0000}s / BPM {compressed.Bpm:0.00}");
                    using var decoder = new PcmDecoder();
                    Require(decoder.Open(path, 88200, 0) && decoder.SeekToMs(12345, accurate: true), "Accurate seek rejected");
                    double[] audio = new double[2048];
                    long frames = 0;
                    int count;
                    while ((count = decoder.Read(audio)) > 0) frames += count;
                    Require(Math.Abs(frames - (40000 - 12345) * 48L) < 48 * 15, $"{format}: retained seek preroll ({frames} frames)");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
        });
        Run("AutoMix: real preloader resamples, adopts and retires both sessions without rebuilding output", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            Set(engine, "_trackTransition", TrackTransitionMode.AutoMix);
            Set(engine, "_dspSettings", new DspSettings());
            using var first = Session.Open(engine, pathA, RenderKind.Pcm, 88200, 0, 100)!;
            first.RequestSeek(15000);
            var source = new GaplessSource(first);
            Set(engine, "_session", first);
            Set(engine, "_gaplessSource", source);
            engine.MusicUrl = pathA;
            engine.IsPlaying = true;
            try
            {
                Require(engine.TryQueueNext(new(first.TimelineEpoch, 81, pathB)), "Plan rejected");
                Require(Preloader(engine).Completion.Wait(10000), "Preparation timed out");
                var next = source.Pending;
                Require(next is { TransitionPlan.BeatAligned: true, SampleRate: 48000, Channels: 2 }, "Resampled beat plan missing");
                double[] audio = new double[2048];
                var timeout = System.Diagnostics.Stopwatch.StartNew();
                while (source.CompletedToken == 0 && timeout.ElapsedMilliseconds < 10000)
                {
                    source.FillPcm(audio, 1024);
                    Thread.Yield();
                }
                engine.SynchronizeGapless();
                Require(engine.MusicUrl == pathB && source.Current == next && source.Outgoing == first, "Engine did not adopt audible mix");
                Require(engine.TryCaptureProgress(out var progress) && progress.GaplessToken == 81
                    && progress.PlaybackRate < 1 && progress.CurrentMs >= next!.TransitionPlan.EntryMs, "Progress clock lost beat rate");
                engine.UpdateDsp(new() { Balance = 0.2 });
                Require(((DspSettings)typeof(PcmEffects).GetField("_settings", Private)!.GetValue(first.Effects)!).Balance == 0.2, "Outgoing DSP stale");
                while (source.IsTransitioning && timeout.ElapsedMilliseconds < 15000)
                {
                    source.FillPcm(audio, 1024);
                    Thread.Yield();
                }
                engine.SynchronizeGapless();
                Require(!source.IsTransitioning && first.DecodeCompletion.Wait(5000), "Outgoing decoder not retired");
                engine.ChangeWaveChannelTime(1000, 91);
                Require(SpinWait.SpinUntil(() => next!.CompletedSeekId == 91, 3000),
                    $"Seek did not clear automatic tempo: completed={next!.CompletedSeekId}, epoch={next.TimelineEpoch}, failure={next.DecodeFailure}");
                Require(next!.AudiblePlaybackRate(0) == 1 && next.CurrentMs == 1000, "Seek retained automatic clock");
            }
            finally
            {
                engine.StopGaplessAsync().GetAwaiter().GetResult();
                Invoke(engine, "DisposeSession");
            }
        });
        Run("AutoMix: exclusive/ASIO preserve native rates and only mix compatible files", () =>
        {
            foreach (string mode in new[] { "WasapiExclusivePush", "WasapiExclusiveEvent", "ASIO" })
            {
                var engine = Engine(mode);
                Set(engine, "_streamLock", new object());
                Set(engine, "_trackTransition", TrackTransitionMode.AutoMix);
                Set(engine, "_dspSettings", new DspSettings());
                using var first = Session.Open(engine, pathA, RenderKind.Pcm, 88200, 0, 100)!;
                first.RequestSeek(15000);
                var source = new GaplessSource(first);
                Set(engine, "_session", first);
                Set(engine, "_gaplessSource", source);
                engine.MusicUrl = pathA;
                engine.IsPlaying = true;
                try
                {
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 91, pathB)), "Mismatch plan rejected too early");
                    Require(Preloader(engine).Completion.Wait(5000) && source.Pending == null
                        && source.Current == first && first.SampleRate == 48000, $"{mode}: rate mismatch was silently resampled");
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 92, pathA)), "Compatible plan rejected");
                    Require(Preloader(engine).Completion.Wait(5000)
                        && source.Pending is { SampleRate: 48000, TransitionPlan.BeatAligned: true }, $"{mode}: same-rate mix disabled");
                }
                finally
                {
                    engine.StopGaplessAsync().GetAwaiter().GetResult();
                    Invoke(engine, "DisposeSession");
                }
            }
        });
        Run("AutoMix: DoP/native DSD and upcoming DSD bitstreams bypass the mixing path", () =>
        {
            foreach (var (mode, kind) in new[] { ("WasapiExclusiveEvent", RenderKind.Dop), ("ASIO", RenderKind.NativeDsd) })
            {
                var engine = Engine(mode);
                Set(engine, "_trackTransition", TrackTransitionMode.AutoMix);
                using var bitstream = Source(engine, kind);
                Set(engine, "_session", bitstream);
                engine.IsPlaying = true;
                Require(!engine.TryQueueNext(new(bitstream.TimelineEpoch, 101, pathA))
                    && typeof(PlaybackEngine).GetField("_gaplessPreloader", Private)!.GetValue(engine) == null,
                    $"{kind}: started PCM analysis/preparation");
                Set(engine, "_session", null);
            }
            WriteDsfFixture();
            string dsd = Path.Combine(AppContext.BaseDirectory, "switch-test.dsf");
            foreach (string mode in new[] { "WasapiExclusiveEvent", "ASIO" })
            {
                var engine = Engine(mode);
                Set(engine, "_trackTransition", TrackTransitionMode.AutoMix);
                using var first = Session.Open(engine, pathA, RenderKind.Pcm, 88200, 0, 100)!;
                first.RequestSeek(15000);
                var source = new GaplessSource(first);
                Set(engine, "_session", first);
                Set(engine, "_gaplessSource", source);
                engine.MusicUrl = pathA;
                engine.IsPlaying = true;
                try
                {
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 102, dsd)), "Bitstream-next plan not accepted");
                    Require(Preloader(engine).Completion.Wait(3000) && source.Pending == null
                        && source.Current == first && !source.IsTransitioning, $"{mode}: upcoming bitstream was decoded/mixed as PCM");
                }
                finally
                {
                    engine.StopGaplessAsync().GetAwaiter().GetResult();
                    Invoke(engine, "DisposeSession");
                }
            }
        });
        Run("AutoMix: album continuation keeps the original ten-second preparation window", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_trackTransition", TrackTransitionMode.AutoMix);
            using var first = BufferedSource(engine, [0.1, 0.1]);
            Set(first, "<TotalMs>k__BackingField", 60000L);
            first.AnchorFrames = first.MsToFrames(40000);
            var source = new GaplessSource(first);
            Set(engine, "_session", first);
            Set(engine, "_gaplessSource", source);
            engine.MusicUrl = pathA;
            engine.IsPlaying = true;
            try
            {
                Require(engine.TryQueueNext(new(first.TimelineEpoch, 111, pathA, true)), "Continuation rejected");
                Require(Preloader(engine).State == GaplessPreparationState.Waiting && Preloader(engine).Completion.IsCompleted,
                    "Album continuation opened a decoder thirty seconds early");
            }
            finally
            {
                engine.StopGaplessAsync().GetAwaiter().GetResult();
                Invoke(engine, "DisposeSession");
            }
        });
    }

    private static void WriteBeatFixture(string path, int rate, double bpm, int seconds)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int frames = rate * seconds, bytes = frames * 2 * sizeof(float);
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)3); writer.Write((short)2); writer.Write(rate);
        writer.Write(rate * 8); writer.Write((short)8); writer.Write((short)32);
        writer.Write("data"u8); writer.Write(bytes);
        for (int i = 0; i < frames; i++)
        {
            double time = i / (double)rate;
            double phase = (time - 0.15) % (60 / bpm);
            double click = time >= 0.15 && phase >= 0 && phase < 0.04
                ? 0.5 * Math.Exp(-phase * 100) * Math.Sin(phase * 2 * Math.PI * 180) : 0;
            float sample = (float)(click + 0.005 * Math.Sin(time * 2 * Math.PI * 1000));
            writer.Write(sample); writer.Write(sample);
        }
    }
}
