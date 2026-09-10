using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

internal static unsafe partial class Program
{
    private static void RunEqualizerQTests()
    {
        Run("EQ: all ten Q values survive IPC roundtrip", () =>
        {
            var request = new UpdateEqRequest { IsEnabled = true, Band5 = 6,
                Q0 = .1f, Q1 = .2f, Q2 = .5f, Q3 = 1, Q4 = 1.414f,
                Q5 = 2, Q6 = 4, Q7 = 8, Q8 = 12, Q9 = 20 };
            byte[] bytes = new byte[BinarySerializer.UpdateEqRequestSize];
            Require(BinarySerializer.WriteUpdateEqRequest(bytes, request) == 81, "wrong packet size");
            var read = BinarySerializer.ReadUpdateEqRequest(bytes);
            float[] actual = [read.Q0, read.Q1, read.Q2, read.Q3, read.Q4, read.Q5, read.Q6, read.Q7, read.Q8, read.Q9];
            Require(actual.SequenceEqual(new[] { .1f, .2f, .5f, 1, 1.414f, 2, 4, 8, 12, 20 }) && read.Band5 == 6,
                "Q or gain corrupted in transit");
            var legacy = BinarySerializer.ReadUpdateEqRequest(bytes.AsSpan(0, 41));
            Require(legacy.Band5 == 6 && legacy.Q0 == EqParameters.DefaultQ && legacy.Q9 == EqParameters.DefaultQ,
                "legacy gain-only payload did not receive default Q");
        });
        Run("EQ: truncated Q payload is rejected", () =>
        {
            try { BinarySerializer.ReadUpdateEqRequest(new byte[60]); }
            catch (ArgumentException) { return; }
            throw new InvalidOperationException("partial packet accepted");
        });
        Run("EQ: larger Q narrows response while preserving center gain", () =>
        {
            double wide = MeasureEq(0.5f, 700), narrow = MeasureEq(8, 700);
            Require(wide > narrow + 2, $"wide={wide}dB narrow={narrow}dB");
            Require(Math.Abs(MeasureEq(.5f, 1000) - 6) < .02 && Math.Abs(MeasureEq(8, 1000) - 6) < .02,
                "Q changed center gain");
        });
        Run("EQ: Q-only IPC change reaches active PCM filter", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            typeof(PlaybackEngine).GetField("EqGains")!.SetValue(engine, new float[10]);
            typeof(PlaybackEngine).GetField("EqQ")!.SetValue(engine, new float[10]);
            using var session = Source(engine, RenderKind.Pcm, 48000);
            Set(engine, "_session", session);
            var request = new UpdateEqRequest { IsEnabled = true, Band5 = 6, Q5 = .5f };
            engine.SetEqualizerState(request);
            double wide = MeasureFilter(session.Eq, 700);
            request.Q5 = 8;
            engine.SetEqualizerState(request);
            double narrow = MeasureFilter(session.Eq, 700);
            Require(wide > narrow + 2 && engine.EqQ[5] == 8, "Q-only change failed to update active filter");
        });
        Run("EQ: matching boost and cut cancel at high Q", () =>
        {
            var boost = new Equalizer(); var cut = new Equalizer();
            float[] gains = new float[10], q = Enumerable.Repeat(12f, 10).ToArray();
            gains[5] = 12; boost.Configure(48000, true, gains, q);
            gains[5] = -12; cut.Configure(48000, true, gains, q);
            double[] samples = new double[48000]; samples[0] = .1;
            boost.Process(samples, samples.Length, 1); cut.Process(samples, samples.Length, 1);
            Require(Math.Abs(samples[0] - .1) < 1e-10 && samples.Skip(1).All(x => Math.Abs(x) < 1e-10),
                "boost/cut pair not unity");
        });
        Run("EQ: invalid Q and low sample rates stay finite", () =>
        {
            float[] gains = Enumerable.Repeat(12f, 10).ToArray();
            float[] q = [0, -1, float.NaN, float.PositiveInfinity, .001f, 100, .1f, 20, 1, 2];
            foreach (int rate in new[] { 8000, 16000, 44100, 192000 })
            {
                var eq = new Equalizer(); eq.Configure(rate, true, gains, q);
                double[] samples = new double[rate * 2]; samples[0] = .001;
                eq.Process(samples, samples.Length, 1);
                Require(samples.All(x => double.IsFinite(x) && Math.Abs(x) < 100), $"unstable at {rate} Hz");
            }
        });
        Run("EQ: presets preserve independent Q and fill missing legacy Q", () =>
        {
            var bands = EqualizerHelper.CreateDefaultBands();
            for (int i = 0; i < 10; i++) bands[i].Q = .1 + i;
            var parsed = EqualizerHelper.Parse(EqualizerHelper.Serialize(EqualizerHelper.Snapshot("Q test", bands)))!;
            Require(parsed.Bands.Select(x => x.Q).SequenceEqual(bands.Select(x => x.Q)), "preset lost Q values");
            var legacy = EqualizerHelper.Parse("{\"Version\":1,\"Bands\":[{\"FrequencyHz\":1000,\"GainDb\":6}]}")!;
            Require(legacy.Bands[5].GainDb == 6 && Math.Abs(legacy.Bands[5].Q - 1.414) < 1e-5, "legacy preset default Q wrong");
            bands[0].Q = double.NaN; bands[1].Q = 100; bands[2].Q = .001;
            var clean = EqualizerHelper.Snapshot("clean", bands);
            Require(clean.Bands[0].Q == EqPreset.DefaultQ && clean.Bands[1].Q == 20 && clean.Bands[2].Q == .1,
                "invalid preset Q not normalized");
        });
    }

    private static double MeasureEq(float qValue, int frequency)
    {
        var eq = new Equalizer(); float[] gains = new float[10], q = new float[10];
        gains[5] = 6; q[5] = qValue; eq.Configure(48000, true, gains, q);
        return MeasureFilter(eq, frequency);
    }

    private static double MeasureFilter(Equalizer eq, int frequency)
    {
        double[] samples = new double[48000];
        for (int i = 0; i < samples.Length; i++) samples[i] = .01 * Math.Sin(2 * Math.PI * frequency * i / 48000);
        eq.Process(samples, samples.Length, 1);
        double energy = 0;
        for (int i = 24000; i < samples.Length; i++) energy += samples[i] * samples[i];
        return 10 * Math.Log10(energy / 24000 / .00005);
    }
}
