using System.Globalization;
using System.Numerics;

namespace BassPlayerIpc.Shared;

public enum ConvolutionSource : byte { Automatic, Curve, Wave }
public readonly record struct CurvePoint(double Frequency, double GainDb);

/// <summary>Canonical, bounded control points; the persisted string has value equality across IPC/JSON.</summary>
public static class CorrectionCurve
{
    public const string Flat = "20,0;20000,0";
    public const int MaxPoints = 32;
    public static CurvePoint[] Parse(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 2048) throw new ArgumentException("Invalid curve.");
        var items = text.Split(';');
        if (items.Length is < 2 or > MaxPoints) throw new ArgumentException("Use 2–32 curve points.");
        var points = new CurvePoint[items.Length];
        double previous = 0;
        for (int i = 0; i < items.Length; i++)
        {
            var pair = items[i].Split(',');
            if (pair.Length != 2 || !double.TryParse(pair[0], CultureInfo.InvariantCulture, out double hz)
                || !double.TryParse(pair[1], CultureInfo.InvariantCulture, out double db)
                || !double.IsFinite(hz) || !double.IsFinite(db) || hz < 20 || hz > 20000 || hz - previous < 0.02
                || db < -12 || db > 12) throw new ArgumentException("Invalid curve point.");
            points[i] = new(hz, db); previous = hz;
        }
        return points;
    }
    public static string Encode(IEnumerable<CurvePoint> points) => string.Join(";", points.Select(p =>
        p.Frequency.ToString("G17", CultureInfo.InvariantCulture) + "," + p.GainDb.ToString("G17", CultureInfo.InvariantCulture)));
    public static double Evaluate(ReadOnlySpan<CurvePoint> points, double frequency)
    {
        if (frequency <= points[0].Frequency) return points[0].GainDb;
        for (int i = 1; i < points.Length; i++)
        {
            if (frequency > points[i].Frequency) continue;
            double t = Math.Log(frequency / points[i - 1].Frequency) / Math.Log(points[i].Frequency / points[i - 1].Frequency);
            // Smoothstep in log frequency: zero slope at nodes, monotone and no overshoot.
            t = t * t * (3 - 2 * t);
            return points[i - 1].GainDb + t * (points[i].GainDb - points[i - 1].GainDb);
        }
        return points[^1].GainDb;
    }
    public static bool UsesCurve(DspSettings settings) => settings.ConvolutionSource == ConvolutionSource.Curve
        || settings.ConvolutionSource == ConvolutionSource.Automatic && string.IsNullOrEmpty(settings.ImpulsePath);

    public static ImpulseResponse Prepare(DspSettings settings, int rate, CancellationToken cancellation = default) =>
        UsesCurve(settings) ? Generate(settings.CurvePoints, rate, cancellation) : ImpulseResponse.Read(settings.ImpulsePath).AtRate(rate);

    public static ImpulseResponse Generate(string text, int rate, CancellationToken cancellation = default)
    {
        if (rate is < 8000 or > 768000) throw new ArgumentOutOfRangeException(nameof(rate));
        var points = Parse(text);
        if (points.All(p => p.GainDb == points[0].GainDb))
            return new(rate, [[Math.Pow(10, points[0].GainDb / 20)]]);
        const int size = 32768;
        var spectrum = new Complex[size];
        for (int i = 0; i <= size / 2; i++)
        {
            double logMagnitude = Evaluate(points, i * (double)rate / size) * Math.Log(10) / 20;
            spectrum[i] = new(logMagnitude, 0);
            if (i > 0 && i < size / 2) spectrum[size - i] = spectrum[i];
        }
        cancellation.ThrowIfCancellationRequested();
        ResponseMath.Fft(spectrum, true); // real cepstrum
        for (int i = 1; i < size / 2; i++) spectrum[i] *= 2;
        Array.Clear(spectrum, size / 2 + 1, size / 2 - 1);
        ResponseMath.Fft(spectrum, false);
        for (int i = 0; i < size; i++) spectrum[i] = Complex.Exp(spectrum[i]);
        ResponseMath.Fft(spectrum, true);
        cancellation.ThrowIfCancellationRequested();
        var taps = new double[ImpulseResponse.MaxTaps];
        for (int i = 0; i < taps.Length; i++)
        {
            double window = i < taps.Length - 512 ? 1 : 0.5 + 0.5 * Math.Cos(Math.PI * (i - (taps.Length - 512)) / 511);
            taps[i] = spectrum[i].Real * window;
        }
        return new(rate, [taps]);
    }
}

/// <summary>Shared response math: the UI measures the same prepared FIR that the player consumes.</summary>
public static class ResponseMath
{
    public static void Fft(Complex[] data, bool inverse)
    {
        int n = data.Length;
        if (n < 2 || (n & (n - 1)) != 0) throw new ArgumentException("FFT size must be a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            Complex step = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / size);
            for (int start = 0; start < n; start += size)
            {
                Complex root = Complex.One;
                for (int i = 0; i < size / 2; i++, root *= step)
                {
                    Complex a = data[start + i], b = data[start + i + size / 2] * root;
                    data[start + i] = a + b; data[start + i + size / 2] = a - b;
                }
            }
        }
        if (inverse) for (int i = 0; i < n; i++) data[i] /= n;
    }

    public static Complex[] Spectrum(double[] taps)
    {
        var data = new Complex[32768];
        for (int i = 0; i < taps.Length; i++) data[i] = new(taps[i], 0);
        Fft(data, false); return data;
    }
    public static double MagnitudeDb(Complex[] spectrum, int rate, double frequency)
    {
        double bin = Math.Clamp(frequency / rate * spectrum.Length, 0, spectrum.Length / 2.0);
        int i = (int)bin, next = Math.Min(i + 1, spectrum.Length / 2);
        double magnitude = spectrum[i].Magnitude + (spectrum[next].Magnitude - spectrum[i].Magnitude) * (bin - i);
        return 20 * Math.Log10(Math.Max(1e-12, magnitude));
    }
    public static double AutoAttenuationDb(ImpulseResponse impulse)
    {
        double peak = 1;
        foreach (var channel in impulse.Channels)
        {
            var spectrum = Spectrum(channel);
            for (int i = 0; i <= spectrum.Length / 2; i++) peak = Math.Max(peak, spectrum[i].Magnitude);
        }
        return -20 * Math.Log10(peak);
    }
}

public readonly record struct PeakCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static PeakCoefficients Create(double frequency, double gainDb, double q, int rate)
    {
        if (Math.Abs(gainDb) < 0.01 || rate <= 0 || frequency >= rate * 0.5) return new(1, 0, 0, 0, 0);
        double a = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * frequency / rate;
        double alpha = Math.Sin(w) / (2 * EqParameters.NormalizeQ(q)), a0 = 1 + alpha / a;
        return new((1 + alpha * a) / a0, -2 * Math.Cos(w) / a0, (1 - alpha * a) / a0,
            -2 * Math.Cos(w) / a0, (1 - alpha / a) / a0);
    }
    public double ResponseDb(double frequency, int rate)
    {
        Complex z = Complex.FromPolarCoordinates(1, -2 * Math.PI * frequency / rate);
        return 20 * Math.Log10(Math.Max(1e-12, ((B0 + B1 * z + B2 * z * z) / (1 + A1 * z + A2 * z * z)).Magnitude));
    }
}
