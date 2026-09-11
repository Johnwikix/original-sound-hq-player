using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunDspTests()
    {
        Run("DSP: defaults bypass PCM bit-for-bit and allocate nothing on render", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            double[] source = new double[1024];
            var random = new Random(17);
            for (int i = 0; i < source.Length; i++) source[i] = random.NextDouble() * 2 - 1;
            double[] buffer = (double[])source.Clone();
            effects.ApplyInput(buffer, 512);
            effects.ApplyStereo(buffer, 512);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++)
            {
                effects.ApplyInput(buffer, 512);
                effects.ApplyStereo(buffer, 512);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require(allocated == 0, $"Render allocated {allocated} bytes");
            Require(buffer.AsSpan().SequenceEqual(source), "Default effects changed samples");
        });
        Run("DSP: attenuation preserves crest factor and sample ratios", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(new DspSettings { HeadroomDb = -6 });
            double[] samples = [0.8, -0.4, 0.02, -0.01];
            effects.ApplyInput(samples, 2);
            effects.ApplyStereo(samples, 2);
            Require(Math.Abs(samples[0] / 0.8 - Math.Pow(10, -6.0 / 20)) < 1e-14, "Gain mismatch");
            Require(Math.Abs(samples[0] / samples[2] - 40) < 1e-12, "Dynamics changed");
        });
        Run("DSP: stereo routing and mono average", () =>
        {
            using var swap = new PcmEffects(48000, 2);
            swap.Configure(new DspSettings { SwapChannels = true });
            double[] samples = [0.75, -0.25];
            swap.ApplyInput(samples, 1); swap.ApplyStereo(samples, 1);
            Require(samples[0] == -0.25 && samples[1] == 0.75, "Swap incorrect");
            using var mono = new PcmEffects(48000, 2);
            mono.Configure(new DspSettings { Mono = true });
            samples = [0.75, -0.25];
            mono.ApplyInput(samples, 1); mono.ApplyStereo(samples, 1);
            Require(samples[0] == 0.25 && samples[1] == 0.25, "Mono must average rather than sum");
        });
        Run("DSP: crossfeed reaches the other channel and seek clears history", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(new DspSettings { Crossfeed = 0.4 });
            double[] samples = [1, 0];
            effects.ApplyInput(samples, 1); effects.ApplyStereo(samples, 1);
            Require(samples[1] > 0 && samples[0] <= 1, "Crossfeed missing or excessive");
            effects.RequestReset();
            Array.Clear(samples);
            effects.ApplyInput(samples, 1); effects.ApplyStereo(samples, 1);
            Require(samples[0] == 0 && samples[1] == 0, "Seek leaked crossfeed history");
        });
        Run("DSP: active stereo hot path allocates nothing", () =>
        {
            using var effects = new PcmEffects(192000, 2);
            effects.Configure(new DspSettings { Crossfeed = 0.4, StereoWidth = 1.5, Balance = 0.2 });
            double[] samples = new double[1024];
            effects.ApplyInput(samples, 512); effects.ApplyStereo(samples, 512);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { effects.ApplyInput(samples, 512); effects.ApplyStereo(samples, 512); }
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Active render allocated");
        });
        Run("Loudness: 1 kHz reference, gain invariance and channel weighting", () =>
        {
            var stereo = MeasureTone(0.1, 2);
            var mono = MeasureTone(0.1, 1);
            var quieter = MeasureTone(0.05, 2);
            Require(Math.Abs(stereo.IntegratedLufs - (-20.0)) < 0.1, $"Unexpected LUFS: {stereo.IntegratedLufs}");
            Require(Math.Abs(stereo.IntegratedLufs - mono.IntegratedLufs - 10 * Math.Log10(2)) < 1e-8, "Channel weighting");
            Require(Math.Abs(stereo.IntegratedLufs - quieter.IntegratedLufs - 20 * Math.Log10(2)) < 1e-8, "Gain invariance");
            Require(Math.Abs(stereo.SamplePeak - 0.1) < 1e-12, "Peak measurement");
        });
        Run("Loudness: silence, short clips and non-finite input never boost", () =>
        {
            var silence = new LoudnessMeter(48000, 2);
            silence.Add(new double[48000]);
            Require(silence.Finish() == null, "Silence got a gain");
            var shortClip = new LoudnessMeter(48000, 1);
            shortClip.Add([1.0]);
            Require(shortClip.Finish() == null, "Too-short clip got a gain");
            var invalid = new LoudnessMeter(48000, 1);
            double[] bad = new double[24000]; Array.Fill(bad, 0.1); bad[0] = double.NaN;
            invalid.Add(bad);
            Require(invalid.Finish() == null, "Invalid audio accepted");
        });
        Run("Loudness: peak ceiling wins over target without a limiter", () =>
        {
            var result = new LoudnessMeasurement(-30, 0.9);
            double gain = result.GainDb(-18);
            Require(gain < 0, "Boost would clip");
            Require(Math.Abs(result.SamplePeak * Math.Pow(10, gain / 20) - Math.Pow(10, -1.0 / 20)) < 1e-12,
                "Peak ceiling not respected");
        });
        Run("Loudness: real decoder, persistent cache, invalidation and cancellation", () =>
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "dsp-cache-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "reference.wav");
            WriteLoudnessTone(path, 0.1f);
            var result = LoudnessScanner.ScanAsync(path, 48000, 2, 88200, 6, CancellationToken.None, directory).GetAwaiter().GetResult();
            Require(result != null && Math.Abs(result.IntegratedLufs + 20) < 0.1, "Decoded tone differs from reference");
            string cache = Directory.GetFiles(directory, "*.bin").Single();
            DateTime written = File.GetLastWriteTimeUtc(cache);
            var repeat = LoudnessScanner.ScanAsync(path, 48000, 2, 88200, 6, CancellationToken.None, directory).GetAwaiter().GetResult();
            Require(result == repeat && written == File.GetLastWriteTimeUtc(cache), "Cached result wasn't reused");
            WriteLoudnessTone(path, 0.05f);
            File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
            var changed = LoudnessScanner.ScanAsync(path, 48000, 2, 88200, 6, CancellationToken.None, directory).GetAwaiter().GetResult();
            Require(changed != null && Math.Abs(result!.IntegratedLufs - changed.IntegratedLufs - 6.0206) < 0.001,
                "Modified file used stale measurement");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            bool stopped = false;
            try { LoudnessScanner.ScanAsync(path, 48000, 2, 88200, 6, cancelled.Token, directory).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Require(stopped, "Cancelled scan was not stopped");
        });
        Run("DSP IPC: round-trip, version checks and finite parameter bounds", () =>
        {
            Span<byte> bytes = stackalloc byte[DspProtocol.SettingsSize];
            var settings = new DspSettings { NormalizeLoudness = true, HeadroomDb = -3, Balance = -0.5,
                TargetLufs = -20, Crossfeed = 0.4, StereoWidth = 1.3, SwapChannels = true, Mono = true };
            DspProtocol.WriteSettings(bytes, settings);
            Require(DspProtocol.ReadSettings(bytes) == settings, "Settings round-trip");
            bool rejected = false;
            try { DspProtocol.ReadSettings(bytes[..^1]); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Truncated payload accepted");
            var invalid = new DspSettings { TargetLufs = double.NaN, Crossfeed = 10, HeadroomDb = double.PositiveInfinity }.Sanitize();
            Require(invalid.TargetLufs == -18 && invalid.Crossfeed == 0.5 && invalid.HeadroomDb == 0, "Invalid settings not normalized");
            Span<byte> stateBytes = stackalloc byte[DspProtocol.StateSize];
            var state = new DspState(2, false, 2, LoudnessStatus.Off, 0, double.NaN);
            DspProtocol.WriteState(stateBytes, state);
            Require(DspProtocol.ReadState(stateBytes) == state, "State round-trip");
        });
        Run("DSP: actual DSD session bypasses EQ and all effects, PCM restores preference", () =>
        {
            var engine = Engine("ASIO");
            Set(engine, "_streamLock", new object());
            SetPublicArray(engine, "EqGains", new float[10]);
            SetPublicArray(engine, "EqQ", Enumerable.Repeat(EqParameters.DefaultQ, 10).ToArray());
            foreach (var kind in new[] { RenderKind.Dop, RenderKind.NativeDsd })
            {
                using var bitstream = Source(engine, kind);
                Set(engine, "_session", bitstream);
                engine.SetEqualizerState(new UpdateEqRequest { IsEnabled = true });
                engine.UpdateDsp(new DspSettings { NormalizeLoudness = true, Mono = true, Crossfeed = 0.4 });
                Require(bitstream.Effects == null && bitstream.Gain == null, "DSP attached to bitstream");
                Require(!engine.GetDspState().EqualizerActive && engine.GetDspState().RenderKind == (byte)kind,
                    "UI state claimed EQ was active on DSD");
                Require(engine.IsEqualizerEnabled, "Saved preference was discarded");
            }
            using var pcm = Source(engine, RenderKind.Pcm);
            Set(engine, "_session", pcm);
            // 设置仍为 ASIO+DSD 位流，但实际会话已回退 PCM，状态必须跟实际会话。
            Require(engine.GetDspState().EqualizerActive && engine.GetDspState().RenderKind == 0, "PCM didn't restore EQ state");
        });
    }

    private static LoudnessMeasurement MeasureTone(double amplitude, int channels)
    {
        var meter = new LoudnessMeter(48000, channels);
        double[] samples = new double[4800 * channels];
        for (int frame = 0; frame < 4800; frame++)
            for (int ch = 0; ch < channels; ch++) samples[frame * channels + ch] = amplitude * Math.Sin(2 * Math.PI * 1000 * frame / 48000);
        for (int i = 0; i < 30; i++) meter.Add(samples);
        return meter.Finish() ?? throw new InvalidOperationException("Tone measurement missing");
    }

    private static void SetPublicArray(object target, string field, float[] values) =>
        target.GetType().GetField(field)!.SetValue(target, values);

    private static void WriteLoudnessTone(string path, float amplitude)
    {
        using var writer = new BinaryWriter(File.Create(path));
        int bytes = 48000 * 3 * 2 * sizeof(float);
        writer.Write("RIFF"u8); writer.Write(36 + bytes); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)3); writer.Write((short)2); writer.Write(48000);
        writer.Write(48000 * 8); writer.Write((short)8); writer.Write((short)32);
        writer.Write("data"u8); writer.Write(bytes);
        for (int frame = 0; frame < 48000 * 3; frame++)
        {
            float sample = amplitude * (float)Math.Sin(2 * Math.PI * 1000 * frame / 48000);
            writer.Write(sample); writer.Write(sample);
        }
    }
}
