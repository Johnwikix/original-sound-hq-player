using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using System.Diagnostics;

internal static unsafe partial class Program
{
    private static void RunConvolutionTests()
    {
        Run("Convolution: SIMD matches scalar FIR across arbitrary block boundaries and stereo mapping", () =>
        {
            var random = new Random(719);
            foreach (bool useSimd in new[] { false, true })
            foreach (int tapCount in new[] { 1, 3, 17, 128, 129, 256, 8192 })
            {
                double[][] taps = [Enumerable.Range(0, tapCount).Select(_ => random.NextDouble() * 0.01).ToArray(),
                    Enumerable.Range(0, tapCount).Select(_ => random.NextDouble() * -0.01).ToArray()];
                var filter = new ConvolutionFilter(new(48000, taps), 48000, 2, useSimd);
                int frames = tapCount * 2 + 300;
                double[] dry = Enumerable.Range(0, frames * 2).Select(_ => random.NextDouble() - 0.5).ToArray();
                double[] actual = (double[])dry.Clone();
                double mix = 1, gain = 1;
                filter.Process(actual.AsSpan(0, 14), 7, 1, ref mix, ref gain, 0.01);
                filter.Process(actual.AsSpan(14), frames - 7, 1, ref mix, ref gain, 0.01);
                for (int i = 0; i < frames; i++)
                    for (int ch = 0; ch < 2; ch++)
                    {
                        double expected = 0;
                        for (int tap = 0; tap < Math.Min(tapCount, i + 1); tap++) expected += taps[ch][tap] * dry[(i - tap) * 2 + ch];
                        Require(Math.Abs(actual[i * 2 + ch] - expected) < 1e-12, $"FIR mismatch at {i}, {ch}, taps={tapCount}");
                    }
                filter.Reset();
                Array.Clear(actual);
                filter.Process(actual, frames, 1, ref mix, ref gain, 0.01);
                Require(actual.All(x => x == 0), "Reset leaked old FIR history");
            }
        });
        Run("Convolution: mono identity, trim, resampling and length limits", () =>
        {
            var filter = new ConvolutionFilter(new(48000, [[1]]), 48000, 2);
            double[] samples = [0.2, -0.4, 0.6, -0.8];
            double mix = 1, gain = 0.5;
            filter.Process(samples, 2, 0.5, ref mix, ref gain, 0.01);
            Require(samples.AsSpan().SequenceEqual(new double[] { 0.1, -0.2, 0.3, -0.4 }), "Mono mapping or trim wrong");
            double[] impulse = new double[256]; impulse[64] = 1;
            var resampled = new ConvolutionFilter(new(48000, [impulse]), 96000, 1);
            samples = new double[600]; samples[0] = 1; gain = 1;
            resampled.Process(samples, samples.Length, 1, ref mix, ref gain, 0.01);
            Require(Math.Abs(samples.Sum() - 1) < 0.002, "Resampling changed DC gain");
            Require(Array.IndexOf(samples, samples.Max()) == 128, "Resampling changed impulse time");
            bool rejected = false;
            try { _ = new ConvolutionFilter(new(48000, [new double[8192]]), 96000, 2); }
            catch (InvalidDataException) { rejected = true; }
            Require(rejected, "Oversized resampled IR accepted");
        });
        Run("Convolution: WAV validation, async load, failure, bypass and seek", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
            try
            {
                WriteIr(path, [0, 1]);
                var impulse = ImpulseResponse.Read(path);
                Require(impulse.SampleRate == 48000 && impulse.Channels[0][1] == 1, "WAV decode wrong");
                using var effects = new PcmEffects(48000, 2);
                effects.Configure(new() { ConvolutionEnabled = true, ImpulsePath = path, ConvolutionTrimDb = 0 });
                Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 3000), "IR load timed out");
                double[] samples = new double[2400];
                effects.ApplyInput(samples, 1200); effects.ApplyConvolution(samples, 1200); // finish wet ramp
                samples[0] = 0.25; samples[1] = -0.5;
                effects.ApplyInput(samples, 1200); effects.ApplyConvolution(samples, 1200);
                Require(samples[0] == 0 && samples[1] == 0 && samples[2] == 0.25 && samples[3] == -0.5, "Delayed IR wrong");
                effects.RequestReset(); Array.Clear(samples);
                effects.ApplyInput(samples, 1200); effects.ApplyConvolution(samples, 1200);
                Require(samples.All(x => x == 0), "Seek leaked convolution history");
                effects.Configure(new() { ConvolutionEnabled = true, ImpulsePath = path + ".missing" });
                Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Failed, 3000), "Missing file failure was not reported");
                effects.Configure(new() { ConvolutionEnabled = false, ImpulsePath = path + ".missing" });
                Require(effects.GetState(0, false).Convolution == ConvolutionStatus.Off, "Disabled status wrong");
                WriteIr(path, [double.NaN]);
                bool rejected = false;
                try { ImpulseResponse.Read(path); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Non-finite IR accepted");
                WriteIr(path, new double[8193]);
                rejected = false;
                try { ImpulseResponse.Read(path); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Long IR accepted");
            }
            finally { File.Delete(path); }
        });
        Run("Convolution: settings and status IPC round-trip within request capacity", () =>
        {
            var settings = new DspSettings { ConvolutionEnabled = true, ImpulsePath = @"C:\校正\headphone.wav", ConvolutionTrimDb = -9.5 };
            byte[] payload = new byte[DspProtocol.SettingsSize];
            Require(payload.Length + IpcConstants.EnvelopeHeaderSize <= IpcConstants.MaxRequestSize, "IPC payload too large");
            DspProtocol.WriteSettings(payload, settings);
            Require(DspProtocol.ReadSettings(payload) == settings, "IR settings lost in IPC");
            var state = new DspState(0, false, 2, LoudnessStatus.Off, 0, 0, true, ConvolutionStatus.Failed);
            payload = new byte[DspProtocol.StateSize]; DspProtocol.WriteState(payload, state);
            Require(DspProtocol.ReadState(payload) == state, "IR state lost in IPC");
        });
        Run("Convolution: 8192-tap stereo renderer allocates no memory", () =>
        {
            double[] impulse = new double[8192]; impulse[0] = 1;
            double[] medians = new double[2];
            foreach (bool simd in new[] { false, true })
            {
                var filter = new ConvolutionFilter(new(192000, [impulse]), 192000, 2, simd);
                double[] samples = new double[1024];
                double mix = 1, gain = 1;
                for (int i = 0; i < 100; i++) filter.Process(samples, 512, 1, ref mix, ref gain, 0.001);
                double[] timings = new double[7];
                for (int round = 0; round < timings.Length; round++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    for (int i = 0; i < 100; i++) filter.Process(samples, 512, 1, ref mix, ref gain, 0.001);
                    timings[round] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    Require(GC.GetAllocatedBytesForCurrentThread() == before, "FIR renderer allocated");
                }
                Array.Sort(timings);
                medians[simd ? 1 : 0] = timings[timings.Length / 2];
            }
            Console.WriteLine($"8192-tap stereo FIR (192 kHz, 266.7 ms audio, 7-run median): scalar {medians[0]:F1} ms, SIMD {medians[1]:F1} ms, {medians[0] / medians[1]:F2}x; Vector<double>.Count={System.Numerics.Vector<double>.Count}");
        });
    }

    private static void WriteIr(string path, double[] taps)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(0x46464952); writer.Write(36 + taps.Length * 8); writer.Write(0x45564157);
        writer.Write(0x20746d66); writer.Write(16); writer.Write((short)3); writer.Write((short)1);
        writer.Write(48000); writer.Write(48000 * 8); writer.Write((short)8); writer.Write((short)64);
        writer.Write(0x61746164); writer.Write(taps.Length * 8);
        foreach (double tap in taps) writer.Write(tap);
    }
}
