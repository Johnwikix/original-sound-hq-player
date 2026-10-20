using AudioPlayer.Decode;

namespace AudioPlayer.Playback;

internal readonly record struct AutoMixAnalysis(bool Supported, double Duration, double SoundStart,
    double SoundEnd, double Bpm, double Confidence, double Phase, int SourceSampleRate = 0);

/// <summary>Bounded head/tail analysis on the preloader worker. No full-track PCM or model runtime is retained.</summary>
internal static class AutoMixAnalyzer
{
    internal const int Rate = 12000;
    internal const int EnvelopeRate = 200;
    internal const int WindowSeconds = 24;

    internal static AutoMixAnalysis Analyze(string path, bool outro, bool beats, CancellationToken cancellation, int requiredSourceRate = 0)
    {
        cancellation.ThrowIfCancellationRequested();
        using var decoder = new PcmDecoder();
        if (!decoder.Open(path, 88200, 0, forceRate: Rate, forceChannels: 1)) return default;
        if (!decoder.CanSeek || decoder.SourceChannels != 2 || decoder.IsDsdSource || decoder.TotalMs <= 0
            || requiredSourceRate > 0 && decoder.SourceSampleRate != requiredSourceRate)
            return default(AutoMixAnalysis) with { SourceSampleRate = decoder.SourceSampleRate };
        double duration = decoder.TotalMs / 1000.0;
        double start = outro ? Math.Max(0, duration - WindowSeconds) : 0;
        if (start > 0 && !decoder.SeekToMs((long)Math.Round(start * 1000), accurate: true)) return default;
        // Fixed upper bounds: 37.5 KiB envelopes + 32 KiB decoder scratch, independent of source rate/length.
        var energy = new float[WindowSeconds * EnvelopeRate];
        var onset = new float[energy.Length];
        var scratch = new double[4096];
        int count = 0, inBin = 0;
        double sum = 0;
        int binFrames = Rate / EnvelopeRate;
        while (count < energy.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            int frames = decoder.Read(scratch);
            if (frames <= 0) break;
            for (int i = 0; i < frames && count < energy.Length; i++)
            {
                double sample = double.IsFinite(scratch[i]) ? scratch[i] : 0;
                sum += sample * sample;
                if (++inBin != binFrames) continue;
                energy[count++] = (float)Math.Sqrt(sum / binFrames);
                sum = 0;
                inBin = 0;
            }
        }
        if (count == 0) return default;
        return Measure(energy.AsSpan(0, count), onset.AsSpan(0, count), start, duration, beats)
            with { SourceSampleRate = decoder.SourceSampleRate };
    }

    internal static AutoMixAnalysis Measure(ReadOnlySpan<float> energy, Span<float> onset,
        double start, double duration, bool beats)
    {
        double peak = 0;
        for (int i = 0; i < energy.Length; i++) peak = Math.Max(peak, energy[i]);
        double floor = Math.Max(0.00001, peak * 0.001);
        int first = 0, last = energy.Length - 1;
        while (first <= last && energy[first] < floor) first++;
        while (last >= first && energy[last] < floor) last--;
        if (first > last) return new(true, duration, start, start, 0, 0, start);
        double soundStart = start + first / (double)EnvelopeRate;
        double soundEnd = start + (last + 1) / (double)EnvelopeRate;
        if (!beats || energy.Length < EnvelopeRate * 8)
            return new(true, duration, soundStart, soundEnd, 0, 0, start);

        double previous = 0, total = 0;
        for (int i = 0; i < energy.Length; i++)
        {
            // Positive energy changes suppress sustained notes and emphasize transients.
            double value = Math.Max(0, energy[i] - previous);
            previous = 0.65 * energy[i] + 0.35 * previous;
            onset[i] = (float)value;
            total += value;
        }
        if (total < 1e-8) return new(true, duration, soundStart, soundEnd, 0, 0, start);
        int bestLag = 0;
        double best = 0, bestCorrelation = 0;
        for (int lag = 60; lag <= 200; lag++)
        {
            double dot = 0, aa = 0, bb = 0;
            for (int i = lag; i < energy.Length; i++)
            {
                double a = onset[i], b = onset[i - lag];
                dot += a * b;
                aa += a * a;
                bb += b * b;
            }
            double correlation = dot / Math.Sqrt(aa * bb + 1e-30);
            double bpm = 60.0 * EnvelopeRate / lag;
            // A weak 120 BPM prior resolves half/double-time ambiguity without overruling clear pulses.
            double score = correlation * (1 - 0.08 * Math.Abs(Math.Log2(bpm / 120)));
            if (score <= best) continue;
            best = score;
            bestCorrelation = correlation;
            bestLag = lag;
        }
        if (bestLag == 0) return new(true, duration, soundStart, soundEnd, 0, 0, start);
        double bestPeriod = bestLag, coherence = 0, phase = 0;
        // Refine the period below one envelope bin; long windows otherwise accumulate noticeable phase drift.
        for (int step = -10; step <= 10; step++)
        {
            double period = bestLag + step * 0.05;
            double real = 0, imaginary = 0;
            for (int i = 0; i < energy.Length; i++)
            {
                double angle = 2 * Math.PI * i / period;
                real += onset[i] * Math.Cos(angle);
                imaginary += onset[i] * Math.Sin(angle);
            }
            double strength = Math.Sqrt(real * real + imaginary * imaginary) / total;
            if (strength <= coherence) continue;
            coherence = strength;
            bestPeriod = period;
            phase = Math.Atan2(imaginary, real) * period / (2 * Math.PI);
        }
        if (phase < 0) phase += bestPeriod;
        double confidence = Math.Min(bestCorrelation, coherence);
        return new(true, duration, soundStart, soundEnd, 60 * EnvelopeRate / bestPeriod,
            confidence, start + phase / EnvelopeRate);
    }
}

internal readonly record struct AutoMixPlan(long ExitMs, long EntryMs, int OverlapMs, double Rate, int RampMs, bool BeatAligned)
{
    internal bool IsMix => OverlapMs > 0;
    internal TempoEnvelope Envelope(int sampleRate) => BeatAligned && Math.Abs(Rate - 1) > 0.0001
        ? new(Rate, (long)OverlapMs * sampleRate / 1000, (long)RampMs * sampleRate / 1000) : default;
}

internal static class AutoMixPlanner
{
    internal static AutoMixPlan Plan(AutoMixAnalysis a, AutoMixAnalysis b, bool alignBeats)
    {
        if (!a.Supported || !b.Supported || a.Duration < 4 || b.Duration < 4
            || a.SoundEnd <= a.SoundStart || b.SoundEnd <= b.SoundStart) return default;
        double overlap = Math.Min(6, Math.Min(a.Duration / 3, b.Duration / 3));
        double exit = Math.Max(0, a.SoundEnd - overlap);
        double entry = b.SoundStart;
        double rate = 1;
        bool aligned = false;
        if (alignBeats && a.Confidence >= 0.6 && b.Confidence >= 0.6 && a.Bpm > 0 && b.Bpm > 0)
        {
            rate = a.Bpm / b.Bpm;
            foreach (double multiplier in (ReadOnlySpan<double>)[0.5, 2])
            {
                double candidate = a.Bpm / (b.Bpm * multiplier);
                if (Math.Abs(candidate - 1) < Math.Abs(rate - 1)) rate = candidate;
            }
            if (Math.Abs(rate - 1) <= 0.06)
            {
                double beatA = 60 / a.Bpm, beatB = 60 / b.Bpm;
                int beats = Math.Max(2, Math.Min(8, (int)(overlap / beatA)));
                overlap = beats * beatA;
                exit = a.Phase + Math.Floor((a.SoundEnd - overlap - a.Phase) / beatA) * beatA;
                entry = b.Phase + Math.Ceiling((b.SoundStart - b.Phase) / beatB) * beatB;
                aligned = exit >= Math.Max(0, a.Duration - AutoMixAnalyzer.WindowSeconds)
                    && entry >= 0 && entry < Math.Min(8, b.Duration / 3);
            }
        }
        if (!aligned)
        {
            rate = 1;
            exit = Math.Max(0, a.SoundEnd - overlap);
            entry = b.SoundStart;
        }
        overlap = Math.Min(overlap, (b.Duration - entry) / Math.Max(1, rate) - 0.5);
        if (overlap < 0.5) return default;
        return new((long)Math.Round(exit * 1000), (long)Math.Round(entry * 1000),
            (int)Math.Round(overlap * 1000), rate, aligned ? 4000 : 0, aligned);
    }
}
