using AudioPlayer.Decode;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using System.Reflection;

internal static unsafe partial class Program
{
    private static void CheckPublishedPcmPlayer(string executable)
    {
        string scope = "pcm-publish-" + Guid.NewGuid().ToString("N");
        using var alive = new Mutex(true, "WinUIMusicPlayer_SingleInstanceMutex_" + scope);
        var start = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment["ORIGINALSOUND_IPC_SCOPE"] = scope;
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(15000);
            using (var client = PipeCommandClient.ConnectAsync("OriginalSound_Audio_Control_v2_" + scope, timeout.Token).GetAwaiter().GetResult())
            {
                byte[] payload = new byte[DspProtocol.SettingsSize], response = new byte[512];
                DspProtocol.WriteSettings(payload, new() { ResampleMode = PcmResampleMode.IntegerMultiple,
                    ResampleTargetRate = 192000, TruePeakLimiterEnabled = true, DitherEnabled = true, CrossfeedCutoffHz = 1200 });
                Require(client.RequestAsync(CommandId.UpdateDsp, payload, response).GetAwaiter().GetResult().Type == MessageTypeId.Success,
                    "NativeAOT player rejected DSP v7");
                var state = client.RequestAsync(CommandId.GetDspState, [], response).GetAwaiter().GetResult();
                Require(state.Type == MessageTypeId.DspState && DspProtocol.ReadState(response.AsSpan(0, state.Length)).IsEnabled,
                    "NativeAOT player state query failed");
                payload[1631] = 2;
                Require(client.RequestAsync(CommandId.UpdateDsp, payload, response).GetAwaiter().GetResult().Type == MessageTypeId.Failed,
                    "NativeAOT player accepted malformed settings");
            }
            alive.ReleaseMutex();
            Require(process.WaitForExit(10000) && process.ExitCode == 0, "NativeAOT player did not release its service");
        }
        finally
        {
            if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); }
            Console.WriteLine(output.GetAwaiter().GetResult()); Console.WriteLine(errors.GetAwaiter().GetResult());
        }
    }

    private delegate void EndpointWriter(double[] source, byte* destination, int samples, int format);
    private delegate void AsioSampleWriter(byte* destination, int type, int frame, double sample);

    private static void RunPcmDspUpgradeTests()
    {
        RunDspMemoryLifetimeTests();
        RunIrMemoryTests();
        Run("PCM: exhaustive 16-bit and boundary/random 24/32-bit integer round-trip", () =>
        {
            for (int sample = short.MinValue; sample <= short.MaxValue; sample++)
                Require(PcmQuantizer.Quantize(sample / 32768.0, 16) == sample, "16-bit round-trip mismatch");
            var random = new Random(19217);
            foreach (int bits in new[] { 18, 20, 24, 32 })
            {
                long scale = 1L << (bits - 1);
                foreach (long sample in new[] { -scale, -scale + 1, -1, 0, 1, scale / 2, scale - 1 })
                    Require(PcmQuantizer.Quantize(sample / (double)scale, bits) == sample, $"{bits}-bit boundary mismatch");
                for (int i = 0; i < 10000; i++)
                {
                    long sample = random.NextInt64(-scale, scale);
                    Require(PcmQuantizer.Quantize(sample / (double)scale, bits) == sample, "Random PCM mismatch");
                }
                Require(PcmQuantizer.Quantize(2, bits) == scale - 1 && PcmQuantizer.Quantize(-2, bits) == -scale, "Saturation wrong");
                foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                    Require(PcmQuantizer.Quantize(invalid, bits) == 0, "Non-finite output");
            }
        });
        Run("PCM: actual FFmpeg decoding to WASAPI and ASIO packed integer bytes", CheckPcmEndpointRoundTrip);
        Run("PCM: optional TPDF is bounded, unbiased and exact silence", () =>
        {
            var q = new PcmQuantizer(); double sum = 0; bool changed = false;
            for (int i = 0; i < 100000; i++)
            {
                int value = q.Convert(0.25, 16, true);
                Require(Math.Abs(value - 8192) <= 1, "Dither exceeded one LSB");
                changed |= value != 8192; sum += value - 8192;
                Require(q.Convert(0, 16, true) == 0, "Dither contaminated silence");
            }
            Require(changed && Math.Abs(sum / 100000) < 0.02, "Biased or inactive TPDF");
        });
        Run("DSP v7: fields, legacy v6 defaults and malformed flags", () =>
        {
            var settings = new DspSettings { ResampleMode = PcmResampleMode.Fixed, ResampleTargetRate = 96000,
                TruePeakLimiterEnabled = true, TruePeakCeilingDb = -2, DitherEnabled = true, CrossfeedCutoffHz = 1400 };
            byte[] bytes = new byte[DspProtocol.SettingsSize]; DspProtocol.WriteSettings(bytes, settings);
            Require(DspProtocol.ReadSettings(bytes) == settings, "New DSP fields lost");
            byte[] old = bytes[..1630]; old[0] = 6;
            var legacy = DspProtocol.ReadSettings(old);
            Require(legacy.ResampleMode == PcmResampleMode.Source && !legacy.TruePeakLimiterEnabled
                && !legacy.DitherEnabled && legacy.CrossfeedCutoffHz == 700, "Legacy defaults changed");
            bytes[1631] = 2; bool rejected = false;
            try { DspProtocol.ReadSettings(bytes); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Invalid limiter flag accepted");
            Require((settings with { CrossfeedCutoffHz = double.NaN, TruePeakCeilingDb = double.PositiveInfinity }).Sanitize()
                is { CrossfeedCutoffHz: 700, TruePeakCeilingDb: -1 }, "Invalid settings not normalized");
        });
        Run("Resampling: source/fixed/integer policies and real decode/EOF/seek", CheckPcmResampling);
        Run("Convolution: long multichannel sparse reference across all FFT tiers", CheckLongConvolution);
        Run("Convolution: explicit channel matching and duration/total-tap budgets", () =>
        {
            foreach (var (impulse, channels) in new (ImpulseResponse, int)[] {
                (new(48000, [[1], [1]]), 6), (new(48000, [new double[96001]]), 1),
                (new(192000, [new double[200000]]), 6) })
            {
                bool rejected = false;
                try { ConvolutionFilter.Prepare(impulse, impulse.SampleRate, channels); } catch (InvalidDataException) { rejected = true; }
                Require(rejected, "Ambiguous IR mapping or budget overflow accepted");
            }
            bool layoutRejected = false;
            try { ConvolutionFilter.Prepare(new(48000, [[1], [1], [1], [1], [1], [1]]) { ChannelMask = 0x3F }, 48000, 6, 0x60F); }
            catch (InvalidDataException) { layoutRejected = true; }
            Require(layoutRejected, "Declared IR speaker layout mismatch accepted");
            var high = ConvolutionFilter.Prepare(new(48000, [new double[48000]]), 192000, 2);
            Require(high.TailFrames == 191999, "One-second IR failed at 192 kHz");
        });
        Run("True Peak: detects intersample peaks across block boundaries and silence", () =>
        {
            var meter = new TruePeakMeter(2);
            double[] samples = new double[8192];
            for (int i = 0; i < samples.Length / 2; i++) samples[i * 2] = samples[i * 2 + 1] = Math.Sin(i * Math.PI / 2 + Math.PI / 4);
            meter.Add(samples.AsSpan(0, 14)); meter.Add(samples.AsSpan(14));
            double peak = meter.Finish();
            Require(peak > 0.95 && samples.Max(Math.Abs) < 0.72, "Intersample peak missed");
            meter.Reset(); meter.Add(new double[1024]); Require(meter.Finish() == 0, "Meter reset leaked history");
        });
        Run("True Peak limiter: linked channels, ceiling, finite output and zero render allocation", CheckTruePeakLimiter);
        Run("Shared peak ceiling: normalization, final limiter and state use the saved value", () =>
        {
            var measurement = new LoudnessMeasurement(-20, 0.5, 0.8);
            foreach (double ceiling in new[] { -12.0, -6, -1, -0.1 })
            foreach (bool limiter in new[] { false, true })
            {
                using var effects = new PcmEffects(48000, 2);
                Set(effects, "_measurement", measurement);
                effects.Configure(new() { NormalizeLoudness = true, TargetLufs = -8, TruePeakCeilingDb = ceiling,
                    TruePeakLimiterEnabled = limiter });
                double expected = ceiling - 20 * Math.Log10(0.8);
                Require(Math.Abs(effects.GetState(0, false).GainDb - expected) < 1e-12, "State ignored the shared ceiling");
                double[] block = [0.8, 0.8];
                effects.ApplyInput(block, 1);
                Require(Math.Abs(20 * Math.Log10(block[0]) - ceiling) < 1e-10, "Normalization ignored the shared ceiling");
                effects.Configure(new() { NormalizeLoudness = true, TargetLufs = -8, TruePeakCeilingDb = -3,
                    TruePeakLimiterEnabled = limiter });
                Require(Math.Abs(effects.GetState(0, false).GainDb - (-3 - 20 * Math.Log10(0.8))) < 1e-12,
                    "Changing only the ceiling did not update normalization");
                effects.Configure(new() { TruePeakLimiterEnabled = limiter, TruePeakCeilingDb = ceiling });
                effects.ResetRenderState();
                var signal = new double[24000];
                for (int i = 0; i < 20000; i++) signal[i] = 1.5 * Math.Sin(i * Math.PI / 2 + Math.PI / 4);
                effects.ApplyFinal(signal, signal.Length / 2);
                if (limiter)
                {
                    var meter = new TruePeakMeter(2); meter.Add(signal);
                    Require(20 * Math.Log10(meter.Finish()) <= ceiling + 0.02, "Final limiter ignored the shared ceiling");
                }
                else Require(signal[0] != 0, "Disabled limiter changed the signal");
            }
        });
        Run("Loudness: multichannel LFE exclusion and surround weighting", () =>
        {
            const int rate = 48000, frames = rate;
            var mono = new LoudnessMeter(rate, 1); var surround = new LoudnessMeter(rate, 6, channelMask: 0x3F);
            var lfeOnly = new LoudnessMeter(rate, 6, channelMask: 0x3F);
            var side = new LoudnessMeter(rate, 6, channelMask: 0x60F);
            var a = new double[frames]; var b = new double[frames * 6]; var c = new double[b.Length]; var d = new double[b.Length];
            for (int i = 0; i < frames; i++)
            {
                a[i] = Math.Sin(2 * Math.PI * 997 * i / rate) * 0.1;
                b[i * 6] = a[i]; c[i * 6 + 3] = a[i]; d[i * 6 + 4] = a[i];
            }
            mono.Add(a); surround.Add(b); lfeOnly.Add(c); side.Add(d);
            double reference = mono.Finish()!.IntegratedLufs;
            Require(Math.Abs(surround.Finish()!.IntegratedLufs - reference) < 1e-9, "Front-channel loudness changed");
            Require(lfeOnly.Finish() == null, "LFE contributed to integrated loudness");
            Require(Math.Abs(side.Finish()!.IntegratedLufs - reference - 10 * Math.Log10(1.41)) < 1e-9, "Surround weight wrong");
            var backCenter = new LoudnessMeter(rate, 4, channelMask: 0x107);
            var back = new double[frames * 4];
            for (int i = 0; i < frames; i++) back[i * 4 + 3] = a[i];
            backCenter.Add(back);
            Require(Math.Abs(backCenter.Finish()!.IntegratedLufs - reference - 10 * Math.Log10(1.41)) < 1e-9,
                "Back-center surround weight wrong");
        });
        Run("Session: limiter delay, convolution tail and seek drain correctly", CheckPcmDspTail);
        Run("Session: long convolution tail survives EOF and final limiter", CheckLongConvolutionTail);
        Run("Gapless: limiter history joins two tracks without duplicate delay or lost samples", CheckGaplessLimiter);
        Run("Convolution: six-channel long IR render has bounded work and no allocations", CheckLongConvolutionRender);
        Run("Session: new DSP leaves DoP and native DSD bypass untouched", () =>
        {
            foreach (var kind in new[] { RenderKind.Dop, RenderKind.NativeDsd })
            {
                using var session = Source(Engine("ASIO"), kind);
                session.ConfigureDsp(new() { TruePeakLimiterEnabled = true, DitherEnabled = true,
                    ConvolutionEnabled = true, ResampleMode = PcmResampleMode.Fixed });
                Require(session.Effects == null && !session.DitherEnabled && session.DspDelayMs == 0, "DSD entered PCM processing");
            }
        });
        Run("Crossfeed: cutoff parameter changes response without render allocations", () =>
        {
            double Render(double cutoff)
            {
                using var fx = new PcmEffects(48000, 2);
                fx.Configure(new() { Crossfeed = 0.4, CrossfeedCutoffHz = cutoff });
                var block = new double[96000];
                for (int i = 0; i < 48000; i++) block[i * 2] = Math.Sin(2 * Math.PI * 1500 * i / 48000);
                fx.ApplyInput(block, 48000); fx.ApplyStereo(block, 48000);
                double energy = 0; for (int i = 24000; i < 48000; i++) energy += block[i * 2 + 1] * block[i * 2 + 1];
                return energy;
            }
            Require(Render(1800) > Render(250) * 3, "Cutoff parameter had no effect");
        });
    }

    private static void CheckPcmEndpointRoundTrip()
    {
        using var session = Source(Engine("ASIO"), RenderKind.Pcm);
        using var wasapi = new WasapiOutput(true, false);
        var asio = (AsioOutput)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(AsioOutput));
        Set(wasapi, "_source", session); Set(asio, "_source", session);
        var wasapiWrite = typeof(WasapiOutput).GetMethod("ConvertDoubleToEndpoint", Private)!.CreateDelegate<EndpointWriter>(wasapi);
        var asioWrite = typeof(AsioOutput).GetMethod("WriteAsioSample", Private)!.CreateDelegate<AsioSampleWriter>(asio);
        foreach (int bits in new[] { 16, 24, 32 })
        {
            int[] values = bits == 16 ? [short.MinValue, -16384, -1, 0, 1, 16384, short.MaxValue]
                : bits == 24 ? [-8388608, -4194304, -1, 0, 1, 4194304, 8388607]
                : [int.MinValue, -1073741824, -1, 0, 1, 1073741824, int.MaxValue];
            string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
            try
            {
                using (var writer = new BinaryWriter(File.Create(path)))
                {
                    int bytes = values.Length * bits / 8;
                    writer.Write(0x46464952); writer.Write(36 + bytes); writer.Write(0x45564157);
                    writer.Write(0x20746d66); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                    writer.Write(48000); writer.Write(48000 * bits / 8); writer.Write((short)(bits / 8)); writer.Write((short)bits);
                    writer.Write(0x61746164); writer.Write(bytes);
                    foreach (int sample in values)
                        for (int b = 0; b < bits / 8; b++) writer.Write((byte)(sample >> (b * 8)));
                }
                using var decoder = new PcmDecoder(); Require(decoder.Open(path, 88200, 0), "Real PCM decoder failed");
                double[] samples = new double[values.Length]; Require(decoder.Read(samples) == values.Length, "PCM samples missing");
                byte[] output = new byte[values.Length * 4];
                fixed (byte* destination = output)
                {
                    foreach (bool useAsio in new[] { false, true })
                    foreach (bool aligned in bits == 24 ? new[] { false, true } : new[] { false })
                    {
                        int kind = bits == 16 ? 16 : bits == 24 ? aligned ? 25 : 24 : 32;
                        if (useAsio)
                        {
                            int type = bits == 16 ? AsioConstants.AsioStInt16Lsb : bits == 24
                                ? aligned ? AsioConstants.AsioStInt32Lsb24 : AsioConstants.AsioStInt24Lsb : AsioConstants.AsioStInt32Lsb;
                            for (int i = 0; i < samples.Length; i++) asioWrite(destination, type, i, samples[i]);
                        }
                        else
                        {
                            // Endpoint format constants are private; use the production enum metadata.
                            var formatType = typeof(WasapiOutput).GetNestedType("FormatKind", BindingFlags.NonPublic)!;
                            kind = (int)formatType.GetField(bits == 16 ? "Pcm16" : bits == 24 ? aligned ? "Pcm24In32" : "Pcm24Packed" : "Pcm32")!.GetRawConstantValue()!;
                            wasapiWrite(samples, destination, samples.Length, kind);
                        }
                        int stride = aligned || bits == 32 ? 4 : bits / 8;
                        for (int i = 0; i < samples.Length; i++)
                        {
                            int actual = stride == 2 ? *(short*)(destination + i * stride) : stride == 3
                                ? (destination[i * 3] | destination[i * 3 + 1] << 8 | destination[i * 3 + 2] << 16) << 8 >> 8
                                : *(int*)(destination + i * stride);
                            if (aligned) actual >>= 8;
                            Require(actual == values[i], $"{bits}-bit endpoint mismatch: {actual} != {values[i]}, ASIO={useAsio}");
                        }
                    }
                }
            }
            finally { File.Delete(path); }
        }
        double[] invalidSamples = [double.NaN, double.PositiveInfinity, double.NegativeInfinity];
        byte[] invalidOutput = new byte[32];
        fixed (byte* destination = invalidOutput)
        {
            var formats = typeof(WasapiOutput).GetNestedType("FormatKind", BindingFlags.NonPublic)!;
            foreach (string name in new[] { "Pcm16", "Pcm24Packed", "Pcm24In32", "Pcm32", "Float32" })
            {
                invalidOutput.AsSpan().Fill(0xFF);
                int kind = (int)formats.GetField(name)!.GetRawConstantValue()!;
                wasapiWrite(invalidSamples, destination, 3, kind);
                int bytes = name == "Pcm16" ? 6 : name == "Pcm24Packed" ? 9 : 12;
                Require(invalidOutput.AsSpan(0, bytes).IndexOfAnyExcept((byte)0) < 0, $"WASAPI {name} emitted non-finite input");
            }
            foreach (int type in new[] { AsioConstants.AsioStInt16Lsb, AsioConstants.AsioStInt24Lsb,
                AsioConstants.AsioStInt32Lsb24, AsioConstants.AsioStInt32Lsb,
                AsioConstants.AsioStFloat32Lsb, AsioConstants.AsioStFloat64Lsb })
            {
                invalidOutput.AsSpan().Fill(0xFF);
                for (int i = 0; i < 3; i++) asioWrite(destination, type, i, invalidSamples[i]);
                int bytes = type == AsioConstants.AsioStInt16Lsb ? 6 : type == AsioConstants.AsioStInt24Lsb ? 9
                    : type == AsioConstants.AsioStFloat64Lsb ? 24 : 12;
                Require(invalidOutput.AsSpan(0, bytes).IndexOfAnyExcept((byte)0) < 0, $"ASIO type {type} emitted non-finite input");
            }
        }
    }

    private static void CheckPcmResampling()
    {
        Require(PcmResampling.Resolve(PcmResampleMode.IntegerMultiple, 44100, 192000) == 176400, "44.1 kHz series lost");
        Require(PcmResampling.Resolve(PcmResampleMode.IntegerMultiple, 96000, 48000) == 96000, "Integer mode unexpectedly downsampled");
        string path = Path.Combine(Directory.GetCurrentDirectory(), "_tools", "test_tone.wav");
        foreach (var mode in new[] { PcmResampleMode.Source, PcmResampleMode.Fixed, PcmResampleMode.IntegerMultiple })
        {
            using var decoder = new PcmDecoder();
            Require(decoder.Open(path, 88200, 0, resampleMode: mode, resampleTargetRate: 96000), "Resampling decoder failed");
            int expected = PcmResampling.Resolve(mode, 44100, 96000);
            Require(decoder.SampleRate == expected, "Wrong selected output rate");
            double[] buffer = new double[1024 * decoder.Channels]; long frames = 0; int count;
            while ((count = decoder.Read(buffer)) > 0) frames += count;
            Require(Math.Abs(frames * 1000.0 / expected - decoder.TotalMs) < 2, "Resampler tail or duration lost");
            decoder.SeekToMs(0); Require(decoder.Read(buffer) > 0, "EOF seek did not reset resampler");
        }
    }

    private static void CheckLongConvolution()
    {
        const int channels = 6, frames = 18000;
        int[] offsets = [0, 127, 128, 511, 512, 2047, 2048, 8191, 8192, 16383, 16999];
        var taps = new double[channels][];
        var dry = new double[frames * channels]; var random = new Random(997);
        for (int ch = 0; ch < channels; ch++)
        {
            taps[ch] = new double[17000];
            foreach (int offset in offsets) taps[ch][offset] = (ch + 1) * 0.01;
            for (int i = 0; i < 500; i++) dry[i * channels + ch] = random.NextDouble() - 0.5;
        }
        var filter = new ConvolutionFilter(new(48000, taps), 48000, channels);
        var actual = (double[])dry.Clone(); double mix = 1, gain = 1;
        for (int offset = 0; offset < frames; offset += 173)
        {
            int take = Math.Min(173, frames - offset);
            filter.Process(actual.AsSpan(offset * channels, take * channels), take, 1, ref mix, ref gain, 1);
        }
        for (int i = 0; i < frames; i++) for (int ch = 0; ch < channels; ch++)
        {
            double expected = 0;
            foreach (int offset in offsets) if (i >= offset) expected += taps[ch][offset] * dry[(i - offset) * channels + ch];
            Require(Math.Abs(actual[i * channels + ch] - expected) < 1e-11, $"Long FIR mismatch at {i}/{ch}");
        }
    }

    private static void CheckTruePeakLimiter()
    {
        const int rate = 48000;
        var limiter = new TruePeakLimiter(rate, 2);
        var samples = new double[(rate + limiter.DelayFrames + 32) * 2];
        for (int i = 0; i < rate; i++)
        {
            samples[i * 2] = 1.5 * Math.Sin(i * Math.PI / 2 + Math.PI / 4);
            samples[i * 2 + 1] = samples[i * 2] * 0.5;
        }
        for (int offset = 0; offset < samples.Length / 2; offset += 137)
            limiter.Process(samples.AsSpan(offset * 2), Math.Min(137, samples.Length / 2 - offset), -1);
        var meter = new TruePeakMeter(2); meter.Add(samples);
        double peak = meter.Finish(); Require(20 * Math.Log10(peak) <= -0.99, $"Limiter exceeded ceiling: {20 * Math.Log10(peak)}");
        for (int i = 0; i < samples.Length; i += 2)
            Require(double.IsFinite(samples[i]) && samples[i + 1] == samples[i] * 0.5, "Channel linkage changed");
        limiter.Reset(); var silence = new double[1024];
        limiter.Process(silence, 512, -1); Require(silence.All(x => x == 0), "Limiter reset leaked old audio");
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) limiter.Process(silence, 512, -1);
        Require(before == GC.GetAllocatedBytesForCurrentThread(), "Limiter render allocated");
        // A sudden gain step can create a new reconstructed peak before a future transient.
        foreach (int burstRate in new[] { 44100, 48000, 96000 })
        foreach (double frequency in new[] { 997, burstRate * 0.25, burstRate * 0.45 })
        {
            var burstLimiter = new TruePeakLimiter(burstRate, 1);
            var burst = new double[burstRate / 2 + burstLimiter.DelayFrames + 64];
            for (int i = 0; i < burstRate / 2; i++)
                burst[i] = 0.88 * Math.Sin(i * 2 * Math.PI * frequency / burstRate + 0.31);
            burst[burstRate / 4] = 8;
            for (int i = 0; i < burst.Length; i += 127)
                burstLimiter.Process(burst.AsSpan(i), Math.Min(127, burst.Length - i), -1);
            var burstMeter = new TruePeakMeter(1); burstMeter.Add(burst);
            double burstDb = 20 * Math.Log10(burstMeter.Finish());
            Require(burstDb <= -0.99, $"Gain transition generated an intersample peak: {burstRate}/{frequency}: {burstDb} dBTP");
        }
    }

    private static void CheckPcmDspTail()
    {
        var engine = Engine("ASIO");
        using var session = Source(engine, RenderKind.Pcm, 48000, 1);
        var ring = new PcmRing(1, 4096, 0, 0); Set(session, "_pcmRing", ring);
        session.ConfigureDsp(new() { TruePeakLimiterEnabled = true }); session.Gain!.SetImmediately(1);
        double[] input = new double[400]; input[0] = 0.1; input[^1] = 0.2;
        Require(ring.Push(input, input.Length, static () => false), "Ring push failed"); ring.MarkInputEnded();
        var output = new List<double>(); var block = new double[97];
        for (int i = 0; i < 30 && !session.IsDrained; i++)
        {
            int count = session.RenderPcm(block, block.Length); output.AddRange(block.Take(count));
        }
        int delay = session.Effects!.DelayFrames;
        Require(session.IsDrained && session.DspDelayMs == 0 && output.Count == 400 + delay, "Limiter tail length incorrect");
        Require(output[delay] == 0.1 && output[delay + 399] == 0.2, "Lookahead dropped source samples");
        session.RequestSeek(0); session.FillPcm(block, block.Length);
        Require(block.All(x => x == 0), "Seek leaked limiter history");
    }

    private static void CheckLongConvolutionTail()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".wav");
        try
        {
            var ir = new double[4097]; ir[^1] = 1; WriteIr(path, ir);
            using var session = Source(Engine("ASIO"), RenderKind.Pcm, 48000, 1);
            var ring = new PcmRing(1, 2048, 0, 0); Set(session, "_pcmRing", ring);
            session.ConfigureDsp(new() { ConvolutionEnabled = true, ImpulsePath = path, AutoPreamp = false,
                TruePeakLimiterEnabled = true });
            Require(SpinWait.SpinUntil(() => session.Effects!.GetState(0, false).Convolution == ConvolutionStatus.Active, 5000), "Long IR not ready");
            session.Gain!.SetImmediately(1);
            var input = new double[1220]; input[^1] = 0.2;
            ring.Push(input, input.Length, static () => false); ring.MarkInputEnded();
            var output = new List<double>(); var block = new double[97];
            for (int i = 0; i < 100 && !session.IsDrained; i++)
            {
                int count = session.RenderPcm(block, block.Length); output.AddRange(block.Take(count));
            }
            int delay = session.Effects!.DelayFrames;
            Require(session.IsDrained && output.Count == input.Length + 4096 + delay, "Convolution tail truncated or unbounded");
            Require(Math.Abs(output[input.Length - 1 + 4096 + delay] - 0.2) < 1e-12, "Final IR echo lost");
        }
        finally { File.Delete(path); }
    }

    private static void CheckGaplessLimiter()
    {
        var engine = Engine("ASIO");
        double[] a = Enumerable.Range(0, 600).Select(i => (i % 17 - 8) * 0.005).ToArray();
        double[] b = Enumerable.Range(0, 1000).Select(i => (i % 13 - 6) * 0.005).ToArray();
        using var first = BufferedSource(engine, a); using var next = BufferedSource(engine, b);
        var settings = new DspSettings { TruePeakLimiterEnabled = true };
        first.ConfigureDsp(settings); next.ConfigureDsp(settings);
        var source = new GaplessSource(first); Require(source.Queue(next, 99), "Next session rejected");
        var output = new List<double>(); var block = new double[194];
        for (int i = 0; i < 30 && (source.Current != next || !next.IsDrained); i++)
        {
            long before = source.SubmittedFrames; source.FillPcm(block, 97);
            output.AddRange(block.Take((int)(source.SubmittedFrames - before) * 2));
        }
        int delay = next.Effects!.DelayFrames * 2;
        Require(source.CompletedToken == 99 && output.Count == a.Length + b.Length + delay, "Gapless delay duplicated or data lost");
        var expected = new double[delay].Concat(a).Concat(b).ToArray();
        Require(output.SequenceEqual(expected), "Gapless limiter waveform is discontinuous");
    }

    private static void CheckLongConvolutionRender()
    {
        var taps = new double[96000]; taps[0] = 0.5; taps[^1] = 0.1;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var filter = new ConvolutionFilter(new(96000, [taps]), 96000, 6);
        timer.Stop(); double prepareMs = timer.Elapsed.TotalMilliseconds;
        var block = new double[512 * 6]; double mix = 1, gain = 1;
        for (int i = 0; i < 32; i++) filter.Process(block, 512, 1, ref mix, ref gain, 1);
        long before = GC.GetAllocatedBytesForCurrentThread(); timer.Restart();
        long maximum = 0;
        for (int i = 0; i < 100; i++)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            filter.Process(block, 512, 1, ref mix, ref gain, 1);
            maximum = Math.Max(maximum, System.Diagnostics.Stopwatch.GetTimestamp() - start);
        }
        timer.Stop(); long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(bytes == 0, "Long IR audio callback allocated");
        Console.WriteLine($"Long IR: 96000 taps × 6 ch, prepare {prepareMs:F1} ms, 533.3 ms audio in {timer.Elapsed.TotalMilliseconds:F1} ms, maximum block {maximum * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F2} ms, render allocation {bytes} B");
    }
}
