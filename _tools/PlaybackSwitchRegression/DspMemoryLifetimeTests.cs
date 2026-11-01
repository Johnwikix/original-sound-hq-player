using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static object? DspField(PcmEffects effects, string name)
        => typeof(PcmEffects).GetField(name, Private)!.GetValue(effects);

    private static DspSettings MemoryCurve() => new()
    {
        ConvolutionEnabled = true, ConvolutionSource = ConvolutionSource.Curve,
        CurvePoints = "20,0;200,3;1000,6;20000,0", AutoPreamp = false
    };

    private static void AwaitFilter(PcmEffects effects)
        => Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 5000), "Filter preparation timed out");

    private static void RunDspMemoryLifetimeTests()
    {
        Run("DSP memory: limiter buffers are created on first enable and reused", () =>
        {
            using var effects = new PcmEffects(768000, 8);
            effects.Configure(new());
            Require(DspField(effects, "_limiter") == null, "Disabled limiter preallocated a large lookahead buffer");
            effects.Configure(new() { TruePeakLimiterEnabled = true });
            var limiter = DspField(effects, "_limiter");
            Require(limiter != null && effects.DelayFrames > 0, "Enabled limiter was not published before its target");
            effects.Configure(new());
            effects.Configure(new() { TruePeakLimiterEnabled = true });
            Require(ReferenceEquals(limiter, DspField(effects, "_limiter")), "Re-enabling replaced a possibly active limiter");
        });
        Run("DSP memory: bypass cancels preparation and rejects late filters", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(MemoryCurve() with { IsEnabled = false, AutoPreamp = true });
            Require(DspField(effects, "_prepareCancellation") == null && DspField(effects, "_preampCancellation") == null,
                "Master bypass started correction preparation");
            effects.Configure(MemoryCurve());
            var cancellation = (CancellationTokenSource)DspField(effects, "_prepareCancellation")!;
            effects.Configure(MemoryCurve() with { ConvolutionEnabled = false });
            Require(cancellation.IsCancellationRequested, "Disabling convolution did not cancel preparation");
            Thread.Sleep(100);
            Require(DspField(effects, "_filter") == null && DspField(effects, "_spectra") == null,
                "Late preparation republished a bypassed filter");
            effects.Configure(MemoryCurve());
            AwaitFilter(effects);
        });
        Run("DSP memory: gapless adoption releases duplicate workspace and preserves waveform", () =>
        {
            using var first = new PcmEffects(48000, 2);
            using var next = new PcmEffects(48000, 2);
            using var reference = new PcmEffects(48000, 2);
            foreach (var effects in new[] { first, next, reference }) { effects.Configure(MemoryCurve()); AwaitFilter(effects); }
            var prefix = new double[8192]; prefix[^2] = 0.2; prefix[^1] = -0.1;
            foreach (var effects in new[] { first, reference })
            {
                var input = (double[])prefix.Clone();
                effects.ApplyInput(input, input.Length / 2); effects.ApplyConvolution(input, input.Length / 2);
            }
            var transferred = DspField(first, "_renderFilter");
            next.ContinueFrom(first);
            next.CompleteContinuation();
            var actual = new double[1024]; var expected = new double[1024];
            next.ApplyInput(actual, 512); next.ApplyConvolution(actual, 512);
            reference.ApplyInput(expected, 512); reference.ApplyConvolution(expected, 512);
            Require(actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) < 1e-12), "Adoption reset the transferred FIR history");
            Require(ReferenceEquals(DspField(next, "_filter"), transferred) && DspField(next, "_continuedTargetFilter") == null,
                "Adoption retained the unused new filter workspace");
            next.Configure(MemoryCurve() with { ConvolutionEnabled = false });
            next.CompleteContinuation();
            Require(DspField(next, "_filter") == null, "Stale adoption restored a disabled filter");
        });
        Run("DSP memory: master bypass retires rendering references on the audio boundary", () =>
        {
            using var session = BufferedSource(Engine("ASIO"), new double[4096]);
            session.ConfigureDsp(MemoryCurve()); AwaitFilter(session.Effects!);
            var block = new double[512]; session.FillPcm(block, 256);
            Require(DspField(session.Effects!, "_renderFilter") != null, "Fixture never rendered the filter");
            session.ConfigureDsp(MemoryCurve() with { IsEnabled = false });
            session.FillPcm(block, 256);
            Require(DspField(session.Effects!, "_renderFilter") == null && DspField(session.Effects!, "_renderTarget") == null,
                "Bypassed session retained the render workspace");
        });
    }
}
