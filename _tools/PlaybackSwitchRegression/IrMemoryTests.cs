using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunIrMemoryTests()
    {
        Run("IR memory: mono kernels are shared while channel histories remain independent", () =>
        {
            var impulse = new double[8192]; impulse[0] = .5; impulse[^1] = .25;
            var coefficients = ConvolutionFilter.Prepare(new(48000, [impulse]), 48000, 2);
            foreach (var tier in coefficients.Tiers)
                Require(ReferenceEquals(tier.Taps[0], tier.Taps[1]) && ReferenceEquals(tier.Kernels[0], tier.Kernels[1]),
                    "Mono coefficients duplicated per output channel");
            var filter = new ConvolutionFilter(coefficients);
            var input = new double[20000]; input[0] = 1;
            double mix = 1, gain = 1;
            filter.Process(input, 10000, 1, ref mix, ref gain, 1);
            Require(Math.Abs(input[0] - .5) < 1e-12 && Math.Abs(input[8191 * 2] - .25) < 1e-10,
                "Shared coefficients changed the left impulse response");
            for (int i = 1; i < input.Length; i += 2) Require(input[i] == 0, "Shared coefficients also shared history");
        });
        Run("IR memory: file coefficients reuse, optional spectra and same-stamp changes", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "ir-memory-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                WriteIr(path, [.5, 0, .25]);
                var settings = new DspSettings { ConvolutionEnabled = true, ConvolutionSource = ConvolutionSource.Wave,
                    ImpulsePath = path, AutoPreamp = false };
                var first = PreparedCorrectionCache.Get(settings, 48000, 2, default);
                Require(first.Spectra == null, "Manual preamp prepared an unused full response spectrum");
                var next = PreparedCorrectionCache.Get(settings, 48000, 2, default);
                Require(ReferenceEquals(first.Coefficients, next.Coefficients), "Same file rebuilt FFT coefficients");
                var automatic = PreparedCorrectionCache.Get(settings with { AutoPreamp = true }, 48000, 2, default);
                Require(ReferenceEquals(first.Coefficients, automatic.Coefficients) && automatic.Spectra is { Length: 1 },
                    "Enabling automatic preamp did not reuse coefficients and prepare analysis");
                var stamp = File.GetLastWriteTimeUtc(path);
                WriteIr(path, [.25, 0, .125]); File.SetLastWriteTimeUtc(path, stamp);
                var changed = PreparedCorrectionCache.Get(settings, 48000, 2, default);
                Require(!ReferenceEquals(first.Coefficients, changed.Coefficients), "Same-length/same-stamp edit reused stale coefficients");
                using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
                try { PreparedCorrectionCache.Get(settings, 48000, 2, cancelled.Token); throw new Exception("Cancelled request hit the cache"); }
                catch (OperationCanceledException) { }
            }
            finally { File.Delete(path); }
        });
        Run("IR memory: paused master/convolution bypass releases renderer references", () =>
        {
            foreach (bool master in new[] { false, true })
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                using var session = BufferedSource(engine, [.2, -.1]);
                Set(engine, "_session", session);
                Set(engine, "_gaplessSource", new GaplessSource(session));
                var settings = MemoryCurve();
                engine.UpdateDsp(settings); AwaitFilter(session.Effects!);
                var samples = new double[512];
                session.RenderPcm(samples, 256);
                Require(DspField(session.Effects!, "_renderFilter") != null, "No renderer history was established");
                engine.UpdateDsp(master ? settings with { IsEnabled = false } : settings with { ConvolutionEnabled = false });
                Require(DspField(session.Effects!, "_renderFilter") == null && DspField(session.Effects!, "_renderTarget") == null,
                    "Paused renderer retained a disabled filter");
            }
        });
        Run("IR memory: concurrent preparation shares coefficients and separates output formats", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "ir-concurrent-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                WriteIr(path, [.75, 0, .125]);
                var settings = new DspSettings { AutoPreamp = false, ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = path };
                var jobs = Enumerable.Range(0, 8).Select(_ => Task.Run(() => PreparedCorrectionCache.Get(settings, 48000, 2, default))).ToArray();
                Require(Task.WaitAll(jobs, 5000), "Concurrent preparation did not finish");
                var first = jobs[0].Result.Coefficients;
                Require(jobs.All(t => ReferenceEquals(first, t.Result.Coefficients)), "Concurrent cache miss duplicated coefficients");
                Require(!ReferenceEquals(first, PreparedCorrectionCache.Get(settings, 96000, 2, default).Coefficients), "Rate was omitted from cache identity");
                Require(!ReferenceEquals(first, PreparedCorrectionCache.Get(settings, 48000, 6, default, 0x3F).Coefficients), "Channels/layout omitted from identity");
            }
            finally { File.Delete(path); }
        });
        Run("IR memory: byte budget evicts before count limit and clear drops coefficients", () =>
        {
            PreparedCorrectionCache.Clear();
            string path = Path.Combine(Path.GetTempPath(), "ir-budget-" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var taps = new double[262144];
                var settings = new DspSettings { AutoPreamp = false, ConvolutionSource = ConvolutionSource.Wave, ImpulsePath = path };
                ConvolutionFilter.Coefficients? first = null;
                for (int i = 0; i < 3; i++)
                {
                    taps[0] = .25 + i * .1;
                    WriteIr(path, taps);
                    using (var stream = File.OpenWrite(path))
                    using (var writer = new BinaryWriter(stream))
                    {
                        stream.Position = 24;
                        writer.Write(192000); writer.Write(192000 * 8);
                    }
                    var prepared = PreparedCorrectionCache.Get(settings, 192000, 2, default);
                    first ??= prepared.Coefficients;
                    Require(PreparedCorrectionCache.CachedBytes <= PreparedCorrectionCache.MaxCachedBytes, "Cache exceeded byte budget");
                }
                Require(PreparedCorrectionCache.CachedCount < 3, "Only entry count bounded the long IR cache");
                PreparedCorrectionCache.Clear();
                Require(PreparedCorrectionCache.CachedBytes == 0 && PreparedCorrectionCache.CachedCount == 0, "Clear retained a coefficient entry");
                GC.KeepAlive(first);
            }
            finally { File.Delete(path); PreparedCorrectionCache.Clear(); }
        });
        Run("IR memory: unused analysis is weak and live automatic preamp retains it", () =>
        {
            var settings = MemoryCurve() with { AutoPreamp = true };
            var weak = PrepareUnusedIrAnalysis(settings);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(!weak.IsAlive, "Cache strongly retained an unused full spectrum");
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(settings with { AutoPreamp = false }); AwaitFilter(effects);
            Require(DspField(effects, "_spectra") == null, "Manual session allocated analysis");
            effects.Configure(settings); AwaitFilter(effects);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Require(DspField(effects, "_spectra") is System.Numerics.Complex[][], "Automatic preamp failed to prepare/retain its response");
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference PrepareUnusedIrAnalysis(DspSettings settings)
        => new(PreparedCorrectionCache.Get(settings, 48000, 2, default));
}
