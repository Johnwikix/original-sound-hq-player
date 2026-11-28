using AudioPlayer.Playback;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;

internal static unsafe partial class Program
{
    private static void RunRadioPresetTests()
    {
        Run("Radio preset: updates saved stock values and preserves custom settings", () =>
        {
            var old = new DspSettings { CompressorEnabled = true, CompressorThresholdDb = -30,
                CompressorRatio = 5, CompressorMakeupDb = 0, IsEnabled = false, PlaybackRate = 1.25, TruePeakCeilingDb = -2 };
            var upgraded = DspQuickPreset.UpgradeRadioPreset(old, SceneEqualizer.Radio);
            int index = DspQuickPreset.GetIndex(upgraded, SceneEqualizer.Radio);
            Require(index < DspQuickPreset.CustomIndex && upgraded == DspQuickPreset.Apply(old, index)
                && upgraded == old with { CompressorThresholdDb = -24, CompressorMakeupDb = 12 },
                "Saved radio preset did not update only the stock compressor values");
            Require(ReferenceEquals(upgraded, DspQuickPreset.UpgradeRadioPreset(upgraded, SceneEqualizer.Radio)),
                "Updated radio preset was rewritten");
            var custom = old with { CompressorRatio = 4 };
            Require(ReferenceEquals(custom, DspQuickPreset.UpgradeRadioPreset(custom, SceneEqualizer.Radio))
                && ReferenceEquals(old, DspQuickPreset.UpgradeRadioPreset(old, SceneEqualizer.Personal)),
                "A custom or personal preset was changed");

            int clubIndex = Enumerable.Range(0, DspQuickPreset.CustomIndex)
                .Single(i => DspQuickPreset.GetEqualizer(i) == SceneEqualizer.Club);
            var club = DspQuickPreset.Apply(upgraded, clubIndex);
            Require(club.CompressorMakeupDb == 0 && DspQuickPreset.GetIndex(club, SceneEqualizer.Club) == clubIndex,
                "Radio gain leaked into Club");
        });
        Run("Radio preset: keeps comparable broadband loudness and respects the peak ceiling", () =>
        {
            const int rate = 48000, channels = 2;
            int radioIndex = Enumerable.Range(0, DspQuickPreset.CustomIndex)
                .Single(i => DspQuickPreset.GetEqualizer(i) == SceneEqualizer.Radio);
            var original = new DspSettings();
            var radio = DspQuickPreset.Apply(original, radioIndex);
            Require(DspQuickPreset.GetIndex(radio, SceneEqualizer.Radio) == radioIndex, "Radio preset does not round-trip");
            var bands = SceneEqualizerCurves.GetBands(SceneEqualizer.Radio)!;
            double ceiling = Math.Pow(10, radio.TruePeakCeilingDb / 20);
            foreach (double level in new[] { .16, .35, .85 })
            {
                double[] samples = new double[rate * channels * 3];
                for (int frame = 0; frame < samples.Length / channels; frame++)
                {
                    double phase = 2 * Math.PI * frame / rate;
                    double envelope = .7 + .3 * Math.Cos(2.75 * phase);
                    double mid = .23 * Math.Sin(620 * phase) + .17 * Math.Sin(1500 * phase);
                    double bass = .35 * Math.Sin(95 * phase);
                    double treble = .18 * Math.Sin(4900 * phase);
                    samples[frame * 2] = level * envelope * (mid + bass + treble);
                    samples[frame * 2 + 1] = level * envelope * (mid + bass - treble);
                }
                var input = new LoudnessMeter(rate, channels);
                input.Add(samples);

                RenderRadioPreset(samples, rate, channels, radio, bands);

                var output = new LoudnessMeter(rate, channels);
                output.Add(samples);
                var before = input.Finish()!;
                var after = output.Finish()!;
                double change = after.IntegratedLufs - before.IntegratedLufs;
                Console.WriteLine($"Radio level {level:F2}: {before.IntegratedLufs:F1} to {after.IntegratedLufs:F1} LUFS ({change:+0.0;-0.0} LU), peak {20 * Math.Log10(after.SamplePeak):F1} dBFS");
                Require(change >= -4 && change <= 4, "Radio preset loudness differs from the source by more than 4 LU");
                Require(after.SamplePeak <= ceiling + 1e-12, "Radio preset exceeded its sample peak ceiling");
            }
            double[] transient = new double[rate * channels];
            for (int frame = 0; frame < 240; frame++)
            {
                double sample = Math.Sin(2 * Math.PI * 1000 * frame / rate);
                transient[frame * 2] = transient[frame * 2 + 1] = sample;
            }
            RenderRadioPreset(transient, rate, channels, radio, bands);
            Require(transient.All(sample => Math.Abs(sample) <= ceiling + 1e-12),
                "Full-scale midrange burst exceeded the sample peak ceiling");
        });
    }

    private static void RenderRadioPreset(double[] samples, int rate, int channels, DspSettings settings, EqBand[] bands)
    {
        using var effects = new PcmEffects(rate, channels);
        var eq = new Equalizer();
        eq.Configure(rate, true, bands.Select(b => (float)b.GainDb).ToArray(), bands.Select(b => (float)b.Q).ToArray());
        effects.Configure(settings);
        int frames = samples.Length / channels;
        effects.ApplyInput(samples, frames);
        eq.Process(samples, frames, channels);
        effects.ApplyStereo(samples, frames);
        effects.ApplyDynamics(samples, frames);
        effects.ApplyFinal(samples, frames);
    }
}
