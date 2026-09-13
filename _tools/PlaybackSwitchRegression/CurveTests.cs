using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using System.Buffers.Binary;

internal static unsafe partial class Program
{
    private static void RunCurveTests()
    {
        Run("Curve: flat FIR is an exact unit impulse at every output rate", () =>
        {
            foreach (int rate in new[] { 8000, 44100, 48000, 96000, 192000, 384000 })
            {
                var ir = CorrectionCurve.Generate(CorrectionCurve.Flat, rate);
                Require(ir.SampleRate == rate && ir.Channels[0].AsSpan().SequenceEqual(new double[] { 1 }), "Flat curve is not transparent");
            }
        });
        Run("Curve: minimum-phase FIR realizes broad response and preserves headroom", () =>
        {
            const string curve = "20,3;100,3;500,-4;2000,2;8000,-2;20000,0";
            var points = CorrectionCurve.Parse(curve);
            foreach (int rate in new[] { 44100, 48000, 96000, 192000 })
            {
                var ir = CorrectionCurve.Generate(curve, rate);
                var spectrum = ResponseMath.Spectrum(ir.Channels[0]);
                double maxError = 0;
                foreach (double hz in new[] { 50.0, 100, 250, 500, 1000, 2000, 8000, 15000 })
                {
                    double desired = CorrectionCurve.Evaluate(points, hz);
                    double actual = ResponseMath.MagnitudeDb(spectrum, rate, hz);
                    maxError = Math.Max(maxError, Math.Abs(desired - actual));
                }
                Require(maxError < 0.35, $"Response error {maxError:F3} dB at {rate}");
                Require(ir.Channels[0].All(double.IsFinite), "Non-finite FIR");
                double gain = Math.Pow(10, ResponseMath.AutoAttenuationDb(ir) / 20);
                Require(spectrum.Take(spectrum.Length / 2 + 1).Max(x => x.Magnitude) * gain <= 1.00000001, "Auto headroom missed peak");
                double early = ir.Channels[0].Take(1024).Sum(x => x * x), all = ir.Channels[0].Sum(x => x * x);
                Require(early / all > 0.99, "Unexpected linear-phase delay");
            }
        });
        Run("Curve: malformed points rejected, interpolation cannot overshoot", () =>
        {
            foreach (string curve in new[] { "", "20,0", "20,0;20,1", "100,0;20,1", "20,NaN;20000,0", "20,13;20000,0", "0,0;20000,0" })
            {
                bool rejected = false;
                try { CorrectionCurve.Parse(curve); } catch (ArgumentException) { rejected = true; }
                Require(rejected, "Invalid curve accepted: " + curve);
            }
            var points = CorrectionCurve.Parse("20,-12;20000,12");
            for (int i = 0; i < 1000; i++)
                Require(CorrectionCurve.Evaluate(points, 20 * Math.Pow(1000, i / 999.0)) is >= -12 and <= 12, "Interpolation overshot nodes");
        });
        Run("Curve: v4 IPC, legacy WAV migration and output sample rate", () =>
        {
            var settings = new DspSettings { ConvolutionSource = ConvolutionSource.Curve, ConvolutionEnabled = true,
                CurvePoints = "20,0;100,3;20000,-2", AutoConvolutionHeadroom = false };
            byte[] bytes = new byte[DspProtocol.SettingsSize]; DspProtocol.WriteSettings(bytes, settings);
            Require(DspProtocol.ReadSettings(bytes) == settings, "Curve settings round-trip failed");
            Require(bytes.Length + IpcConstants.EnvelopeHeaderSize <= IpcConstants.MaxRequestSize, "Curve request exceeds IPC slot");
            bytes[1082] = 33;
            bool rejected = false;
            try { DspProtocol.ReadSettings(bytes); } catch (ArgumentException) { rejected = true; }
            Require(rejected, "Oversized point list accepted");
            var legacy = new DspSettings { ImpulsePath = "old.wav", ConvolutionEnabled = true };
            DspProtocol.WriteSettings(bytes, legacy); bytes[0] = 3;
            Require(!CorrectionCurve.UsesCurve(DspProtocol.ReadSettings(bytes.AsSpan(0, 1080))), "Legacy WAV became a drawn curve");
            var state = new DspState(0, true, 2, LoudnessStatus.Off, 0, 0, true, ConvolutionStatus.Active, 96000);
            bytes = new byte[DspProtocol.StateSize]; DspProtocol.WriteState(bytes, state);
            Require(DspProtocol.ReadState(bytes) == state, "Output rate lost");
        });
        Run("Curve: EQ and FIR combine as multiplied amplitudes", () =>
        {
            var coefficients = PeakCoefficients.Create(1000, 3, 1.414, 48000);
            double eq = coefficients.ResponseDb(1000, 48000);
            var ir = CorrectionCurve.Generate("20,3;20000,3", 48000);
            double fir = ResponseMath.MagnitudeDb(ResponseMath.Spectrum(ir.Channels[0]), 48000, 1000);
            Require(Math.Abs(eq + fir - 6) < 1e-9, "Combined EQ + FIR response is wrong");
        });
        Run("Curve: live filter replacement crossfades without allocation", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            var initial = new DspSettings { ConvolutionEnabled = true, ConvolutionSource = ConvolutionSource.Curve,
                ConvolutionTrimDb = 0, AutoConvolutionHeadroom = false };
            effects.Configure(initial);
            Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 3000), "Initial curve load timed out");
            var samples = Enumerable.Repeat(0.2, 4096).ToArray();
            effects.ApplyInput(samples, 2048); effects.ApplyConvolution(samples, 2048);
            effects.Configure(initial with { CurvePoints = "20,6;20000,6" });
            Require(SpinWait.SpinUntil(() => effects.GetState(0, false).Convolution == ConvolutionStatus.Active, 3000), "Replacement timed out");
            Array.Fill(samples, 0.2);
            long before = GC.GetAllocatedBytesForCurrentThread();
            effects.ApplyInput(samples, 2048); effects.ApplyConvolution(samples, 2048);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "Replacement allocated on renderer");
            Require(Math.Abs(samples[0] - 0.2) < 0.001 && Math.Abs(samples[^1] - 0.2 * Math.Pow(10, 6 / 20.0)) < 1e-10, "Replacement jumped or failed to settle");
            for (int i = 2; i < samples.Length; i += 2) Require(Math.Abs(samples[i] - samples[i - 2]) < 0.001, "Discontinuity during crossfade");
            effects.RequestReset(); Array.Clear(samples);
            effects.ApplyInput(samples, 2048); effects.ApplyConvolution(samples, 2048);
            Require(samples.All(x => x == 0), "Seek leaked crossfade history");
        });
    }
}
