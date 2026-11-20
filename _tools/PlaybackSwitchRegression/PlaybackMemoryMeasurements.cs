using AudioPlayer.Decode;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using System.Diagnostics;

internal static unsafe partial class Program
{
    private static void MeasurePlaybackAllocations()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "memory-reference.wav");
        WriteLoudnessTone(path, 0.1f);
        double[] buffer = new double[2048];
        foreach (double rate in new[] { 1.0, 0.25, 1.5, 5.0 })
        {
            using var decoder = new PcmDecoder();
            Require(decoder.Open(path, 88200, 0), "Memory decoder open failed");
            long before = GC.GetAllocatedBytesForCurrentThread();
            var tempo = rate == 1 ? null : new TempoProcessor(48000, 2, rate);
            long setupBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            // Warm wrappers/JIT before measuring the decoder-thread block loop.
            if (tempo == null) decoder.Read(buffer); else tempo.Read(decoder, buffer);
            decoder.SeekToMs(0);
            tempo?.Reset();
            before = GC.GetAllocatedBytesForCurrentThread();
            int total = 0, frames;
            while ((frames = tempo == null ? decoder.Read(buffer) : tempo.Read(decoder, buffer)) > 0) total += frames;
            long loopBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"MEMORY rate={rate} setup={setupBytes} frames={total} loopManagedBytes={loopBytes}");
        }
        var dynamics = new DynamicsProcessor(48000, 2);
        var settings = new DspSettings { CompressorEnabled = true };
        dynamics.Process(buffer, 1024, settings);
        long initial = GC.GetAllocatedBytesForCurrentThread();
        for (int block = 0; block < 10000; block++) dynamics.Process(buffer, 1024, settings);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - initial;
        Console.WriteLine($"MEMORY dynamicsBlocks=10000 loopManagedBytes={allocated}");
        foreach (int taps in new[] { 1024, 8192 })
        {
            var impulse = new ImpulseResponse(48000, [new double[taps], new double[taps]]);
            impulse.Channels[0][0] = impulse.Channels[1][0] = 1;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var coefficients = ConvolutionFilter.Prepare(impulse, 48000, 2);
            long preparationBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            before = GC.GetAllocatedBytesForCurrentThread();
            var filter = new ConvolutionFilter(coefficients);
            long historyBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            GC.KeepAlive(filter);
            Console.WriteLine($"MEMORY convolutionTaps={taps} coefficientPreparationBytes={preparationBytes} filterHistoryBytes={historyBytes}");
        }
    }

    private static void MeasureTransitionRenderCallbacks()
    {
        // Drive the production PCM callback without a device. Replenish both SPSC rings outside each timed interval.
        foreach (var (rate, channels, frames, limiter) in new[] { (48000, 2, 480, false), (48000, 2, 480, true), (192000, 6, 1024, false) })
        {
            Measure(rate, channels, frames, limiter, "steady", queued: false, overlap: false);
            Measure(rate, channels, frames, limiter, "ready-standby", queued: true, overlap: false);
            Measure(rate, channels, frames, limiter, "overlap", queued: true, overlap: true);
        }

        static void Measure(int rate, int channels, int frames, bool limiter, string label, bool queued, bool overlap)
        {
            const int warmup = 100, blocksPerRun = 300, runs = 3, blocks = blocksPerRun * runs;
            var engine = Engine("WasapiShared");
            var settings = new DspSettings { TruePeakLimiterEnabled = limiter };
            var output = new double[frames * channels];
            var signal = new double[output.Length];
            for (int i = 0; i < signal.Length; i++) signal[i] = Math.Sin(i * 0.025) * 0.65;
            var times = new long[blocks];
            long allocated = 0;
            Require((warmup + blocksPerRun) * frames < DspSettings.MaxCrossfadeSeconds * rate, "Benchmark must stop before handoff");
            for (int run = 0; run < runs; run++)
            {
                using var first = Source(engine, RenderKind.Pcm, rate, channels);
                using var next = Source(engine, RenderKind.Pcm, rate, channels);
                var firstRing = new PcmRing(channels, frames * 2, 0, 0);
                var nextRing = new PcmRing(channels, frames * 2, 0, 0);
                Set(first, "_pcmRing", firstRing);
                Set(next, "_pcmRing", nextRing);
                Set(first, "<TotalMs>k__BackingField", 120000L);
                Set(next, "<TotalMs>k__BackingField", 120000L);
                first.ConfigureDsp(settings);
                next.ConfigureDsp(settings);
                var source = new GaplessSource(first);
                if (queued) Require(source.Queue(next, 1, TrackTransitionStyle.Crossfade, DspSettings.MaxCrossfadeSeconds, transitionNow: overlap), "Benchmark plan rejected");
                if (queued && !overlap) nextRing.Push(signal, frames, static () => false);
                for (int block = -warmup; block < blocksPerRun; block++)
                {
                    Require(firstRing.ReadyFrames == 0 && (!overlap || nextRing.ReadyFrames == 0), "Renderer did not consume the intended input blocks");
                    firstRing.Push(signal, frames, static () => false);
                    if (overlap) nextRing.Push(signal, frames, static () => false);
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    source.FillPcm(output, frames);
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (block >= 0)
                    {
                        allocated += GC.GetAllocatedBytesForCurrentThread() - before;
                        times[run * blocksPerRun + block] = elapsed;
                    }
                }
                Require(first.FramesPlayed == frames * (warmup + blocksPerRun) && next.FramesPlayed == (overlap ? first.FramesPlayed : 0),
                    "Benchmark missed the intended PCM path");
            }
            Array.Sort(times);
            double micros = 1_000_000.0 / Stopwatch.Frequency;
            Console.WriteLine($"RENDER {rate}Hz/{channels}ch frames={frames} limiter={limiter} {label}: p50={times[blocks / 2] * micros:F2}us p95={times[blocks * 95 / 100] * micros:F2}us p99={times[blocks * 99 / 100] * micros:F2}us max={times[^1] * micros:F2}us totalAllocated={allocated}B/{blocks} callbacks");
        }
    }
}
