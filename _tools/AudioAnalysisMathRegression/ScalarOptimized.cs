using System;
using System.Numerics;

namespace ScalarOptimized;

/// <summary>Extracts a fixed, key-invariant MIR baseline from ten seconds of 32 kHz mono audio.</summary>
/// <remarks>
/// Uses independently written, standard windowed-sinc, FFT, mel/DCT and autocorrelation methods.
/// Owns reusable buffers and is not thread-safe; give each worker its own instance.
/// </remarks>
public sealed class DspExtractor
{
    /// <summary>Gets the number of weighted, normalized output components.</summary>
    public const int Dimension = 128;

    private const int InputLength = 320000;
    private const int SampleRate = 16000;
    private const int Length = InputLength / 2;
    private const int FftSize = 2048;
    private const int Hop = 256;
    private const int Bins = FftSize / 2 + 1;
    private const int MelCount = 32;
    private const int MfccCount = 20;
    private const int FrameCount = (Length - FftSize) / Hop + 1;
    private const int FirLength = 31;
    private const float Tiny = 1e-20f;

    private readonly float[] _signal = new float[Length];
    private readonly float[] _fir = new float[FirLength];
    private readonly float[] _window = new float[FftSize];
    private readonly int[] _reverse = new int[FftSize];
    private readonly float[] _cos = new float[FftSize / 2];
    private readonly float[] _sin = new float[FftSize / 2];
    private readonly float[] _stageCos = new float[FftSize - 1];
    private readonly float[] _stageSin = new float[FftSize - 1];
    private readonly float[] _real = new float[FftSize];
    private readonly float[] _imaginary = new float[FftSize];
    private readonly float[] _power = new float[Bins];
    private readonly float[] _magnitude = new float[Bins];
    private readonly float[] _previousMagnitude = new float[Bins];
    private readonly float[] _contrastScratch = new float[Bins];
    private readonly float[] _mel = new float[MelCount];
    private readonly float[] _dct = new float[MfccCount * MelCount];
    private readonly int[] _melStart = new int[MelCount];
    private readonly int[] _melEnd = new int[MelCount];
    private readonly int[] _melOffset = new int[MelCount];
    private readonly float[] _melWeights;
    private readonly int[] _contrastEdges = [2, 26, 51, 102, 205, 410, Bins];
    private readonly int[] _pitchLeft = new int[Bins];
    private readonly float[] _pitchFraction = new float[Bins];
    private readonly float[] _pitchScale = new float[Bins];
    private readonly float[] _chroma = new float[12];
    private readonly double[] _sum = new double[56];
    private readonly double[] _sumSquares = new double[56];
    private readonly float[] _onsets = new float[FrameCount];
    private readonly float[] _levelWindows = new float[Length / 160];
    private readonly int[] _tempoLagStart = new int[12];
    private readonly int[] _tempoLagEnd = new int[12];
    private int _activeFrames;

    /// <summary>Initializes transform tables and all reusable work buffers.</summary>
    public DspExtractor()
    {
        InitializeFir();
        for (int i = 0; i < FftSize; i++)
        {
            _window[i] = .5f - .5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1));
            int source = i;
            int reversed = 0;
            for (int bit = 0; bit < 11; bit++)
            {
                reversed = (reversed << 1) | (source & 1);
                source >>= 1;
            }
            _reverse[i] = reversed;
        }
        for (int i = 0; i < FftSize / 2; i++)
        {
            _cos[i] = MathF.Cos(-2 * MathF.PI * i / FftSize);
            _sin[i] = MathF.Sin(-2 * MathF.PI * i / FftSize);
        }

        // 将各级旋转因子预排为连续块，让SIMD蝶形避免逐次gather。
        for (int size = 2; size <= FftSize; size <<= 1)
            for (int i = 0; i < size / 2; i++)
            {
                _stageCos[size / 2 - 1 + i] = _cos[i * (FftSize / size)];
                _stageSin[size / 2 - 1 + i] = _sin[i * (FftSize / size)];
            }

        Span<float> melEdges = stackalloc float[MelCount + 2];
        float melLow = HertzToMel(40);
        float melHigh = HertzToMel(7600);
        int weightCount = 0;
        for (int i = 0; i < melEdges.Length; i++)
            melEdges[i] = MelToHertz(melLow + (melHigh - melLow) * i / (MelCount + 1));
        for (int band = 0; band < MelCount; band++)
        {
            _melStart[band] = Math.Clamp((int)MathF.Ceiling(melEdges[band] * FftSize / SampleRate), 1, Bins - 1);
            _melEnd[band] = Math.Clamp((int)MathF.Floor(melEdges[band + 2] * FftSize / SampleRate) + 1, _melStart[band] + 1, Bins);
            _melOffset[band] = weightCount;
            weightCount += _melEnd[band] - _melStart[band];
        }
        _melWeights = new float[weightCount];
        for (int band = 0; band < MelCount; band++)
        {
            float total = 0;
            int offset = _melOffset[band];
            for (int bin = _melStart[band]; bin < _melEnd[band]; bin++)
            {
                float frequency = bin * (float)SampleRate / FftSize;
                float value = frequency < melEdges[band + 1]
                    ? (frequency - melEdges[band]) / (melEdges[band + 1] - melEdges[band])
                    : (melEdges[band + 2] - frequency) / (melEdges[band + 2] - melEdges[band + 1]);
                value = Math.Max(0, value);
                _melWeights[offset + bin - _melStart[band]] = value;
                total += value;
            }
            for (int bin = _melStart[band]; bin < _melEnd[band]; bin++)
                _melWeights[offset + bin - _melStart[band]] /= Math.Max(total, Tiny);
        }
        for (int coefficient = 0; coefficient < MfccCount; coefficient++)
            for (int band = 0; band < MelCount; band++)
                _dct[coefficient * MelCount + band] = MathF.Sqrt(2f / MelCount)
                    * MathF.Cos(MathF.PI * (coefficient + 1) * (band + .5f) / MelCount);

        for (int bin = 1; bin < Bins; bin++)
        {
            float frequency = bin * (float)SampleRate / FftSize;
            if (frequency < 80 || frequency > 5000) continue;
            float pitch = 69 + 12 * MathF.Log2(frequency / 440);
            int lower = (int)MathF.Floor(pitch);
            _pitchLeft[bin] = ((lower % 12) + 12) % 12;
            _pitchFraction[bin] = pitch - lower;
            _pitchScale[bin] = MathF.Sqrt(440 / frequency);
        }
        for (int band = 0; band < 12; band++)
        {
            double lowBpm = 40 * Math.Pow(6, band / 12.0);
            double highBpm = 40 * Math.Pow(6, (band + 1) / 12.0);
            _tempoLagStart[band] = Math.Max(1, (int)Math.Floor(60 * SampleRate / (Hop * highBpm)));
            _tempoLagEnd[band] = Math.Min(FrameCount - 2, (int)Math.Ceiling(60 * SampleRate / (Hop * lowBpm)));
        }
    }

    /// <summary>Extracts a reproducible weighted MIR feature vector into caller-owned memory.</summary>
    /// <param name="samples">Exactly 320,000 mono samples at 32 kHz.</param>
    /// <param name="output">A destination with at least <see cref="Dimension"/> components.</param>
    /// <exception cref="ArgumentException">The input or destination has an incompatible length.</exception>
    /// <remarks>Does not retain the caller's spans. Reusing an instance reuses all work buffers.</remarks>
    public void Extract(ReadOnlySpan<float> samples, Span<float> output)
    {
        if (samples.Length != InputLength) throw new ArgumentException("Expected ten seconds of 32 kHz mono audio.", nameof(samples));
        if (output.Length < Dimension) throw new ArgumentException("The feature destination is too short.", nameof(output));
        output = output[..Dimension];
        output.Clear();
        Array.Clear(_sum);
        Array.Clear(_sumSquares);
        Array.Clear(_previousMagnitude);
        _activeFrames = 0;

        // .NET 10+：所有逐帧/逐样本缓冲均由实例持有，热路径不创建数组、闭包或 LINQ 迭代器。
        Downsample(samples);
        float previousLogEnergy = 0;
        for (int frame = 0; frame < FrameCount; frame++)
        {
            int start = frame * Hop;
            double timeEnergy = 0;
            for (int i = 0; i < FftSize; i++)
            {
                float sample = _signal[start + i];
                timeEnergy += sample * sample;
                int target = _reverse[i];
                _real[target] = sample * _window[i];
                _imaginary[target] = 0;
            }
            float logEnergy = MathF.Log((float)(timeEnergy / FftSize) + Tiny);
            if (timeEnergy <= 1e-12)
            {
                _onsets[frame] = 0;
                Array.Clear(_previousMagnitude);
                previousLogEnergy = logEnergy;
                continue;
            }
            Transform();
            double totalPower = 0;
            double totalMagnitude = 0;
            int binVector = 1;
            if (false)
                for (; binVector <= Bins - Vector<float>.Count; binVector += Vector<float>.Count)
                {
                    var real = new Vector<float>(_real.AsSpan(binVector));
                    var imaginary = new Vector<float>(_imaginary.AsSpan(binVector));
                    var power = real * real + imaginary * imaginary;
                    power.CopyTo(_power.AsSpan(binVector));
                    Vector.SquareRoot(power).CopyTo(_magnitude.AsSpan(binVector));
                }
            for (int bin = binVector; bin < Bins; bin++)
            {
                float power = _real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin];
                _power[bin] = power;
                float magnitude = MathF.Sqrt(power);
                _magnitude[bin] = magnitude;
            }
            // 保留标量累加顺序，限制数值漂移；平方和开方已按频带向量化。
            for (int bin = 1; bin < Bins; bin++)
            {
                totalPower += _power[bin];
                totalMagnitude += _magnitude[bin];
            }
            float flux = AnalyzeSpectrum(start, totalPower, totalMagnitude, frame == 0);
            float energyRise = frame == 0 ? 0 : Math.Clamp((logEnergy - previousLogEnergy) / 4, 0, 1);
            _onsets[frame] = .5f * flux + .5f * energyRise;
            previousLogEnergy = logEnergy;
            AnalyzeMel();
            AnalyzeContrast();
            AnalyzePitch();
            _activeFrames++;
        }

        WriteMoments(0, 20, output, 0);
        WriteMoments(20, 8, output, 40);
        WriteMoments(28, 6, output, 56);
        WriteMoments(34, 6, output, 68);
        WriteMoments(40, 16, output, 80);
        AnalyzeRhythm(output[112..124]);
        AnalyzeDynamics(output[124..128]);

        // 每组先归一化再按固定先验加权，避免仅靠维度数量或绝对调性主导距离。
        WeightGroup(output[..40], .30f);
        WeightGroup(output[40..56], .16f);
        WeightGroup(output[56..68], .12f);
        WeightGroup(output[68..80], .08f);
        WeightGroup(output[80..112], .20f);
        WeightGroup(output[112..124], .10f);
        WeightGroup(output[124..128], .04f);
        Normalize(output);
    }

    private void InitializeFir()
    {
        double sum = 0;
        const double cutoff = .225;
        for (int i = 0; i < FirLength; i++)
        {
            int distance = i - FirLength / 2;
            double sinc = distance == 0 ? 2 * cutoff : Math.Sin(2 * Math.PI * cutoff * distance) / (Math.PI * distance);
            double window = .54 - .46 * Math.Cos(2 * Math.PI * i / (FirLength - 1));
            _fir[i] = (float)(sinc * window);
            sum += _fir[i];
        }
        for (int i = 0; i < FirLength; i++) _fir[i] /= (float)sum;
    }

    private void Downsample(ReadOnlySpan<float> samples)
    {
        for (int target = 0; target < Length; target++)
        {
            int begin = target * 2 - FirLength / 2;
            float value = 0;
            int first = Math.Max(0, -begin);
            int last = Math.Min(FirLength, samples.Length - begin);
            int tap = first;
            // .NET 10+：FIR内积使用硬件SIMD；边界及不支持SIMD的平台保留标量尾部。
            if (false)
                for (; tap <= last - Vector<float>.Count; tap += Vector<float>.Count)
                    value += Vector.Dot(new Vector<float>(samples.Slice(begin + tap)), new Vector<float>(_fir.AsSpan(tap)));
            for (; tap < last; tap++) value += samples[begin + tap] * _fir[tap];
            _signal[target] = float.IsFinite(value) ? Math.Clamp(value, -8, 8) : 0;
        }
    }

    private void Transform()
    {
        for (int size = 2; size <= FftSize; size <<= 1)
        {
            int half = size >> 1;
            int offset = half - 1;
            for (int start = 0; start < FftSize; start += size)
            {
                int i = 0;
                if (false)
                    for (; i <= half - Vector<float>.Count; i += Vector<float>.Count)
                    {
                        int even = start + i, odd = even + half;
                        var cosine = new Vector<float>(_stageCos.AsSpan(offset + i));
                        var sine = new Vector<float>(_stageSin.AsSpan(offset + i));
                        var oddReal = new Vector<float>(_real.AsSpan(odd));
                        var oddImaginary = new Vector<float>(_imaginary.AsSpan(odd));
                        var real = oddReal * cosine - oddImaginary * sine;
                        var imaginary = oddReal * sine + oddImaginary * cosine;
                        var evenReal = new Vector<float>(_real.AsSpan(even));
                        var evenImaginary = new Vector<float>(_imaginary.AsSpan(even));
                        (evenReal - real).CopyTo(_real.AsSpan(odd));
                        (evenImaginary - imaginary).CopyTo(_imaginary.AsSpan(odd));
                        (evenReal + real).CopyTo(_real.AsSpan(even));
                        (evenImaginary + imaginary).CopyTo(_imaginary.AsSpan(even));
                    }
                for (; i < half; i++)
                {
                    int even = start + i, odd = even + half;
                    float cosine = _stageCos[offset + i], sine = _stageSin[offset + i];
                    float real = _real[odd] * cosine - _imaginary[odd] * sine;
                    float imaginary = _real[odd] * sine + _imaginary[odd] * cosine;
                    _real[odd] = _real[even] - real;
                    _imaginary[odd] = _imaginary[even] - imaginary;
                    _real[even] += real;
                    _imaginary[even] += imaginary;
                }
            }
        }
    }

    private float AnalyzeSpectrum(int start, double totalPower, double totalMagnitude, bool first)
    {
        double inversePower = 1 / Math.Max(totalPower, Tiny);
        double inverseMagnitude = 1 / Math.Max(totalMagnitude, Tiny);
        double center = 0;
        double logPower = 0;
        double entropy = 0;
        double highPower = 0;
        float flux = 0;
        float rolloff = 0;
        double cumulative = 0;
        for (int bin = 1; bin < Bins; bin++)
        {
            double probability = _power[bin] * inversePower;
            center += bin * probability;
            logPower += Math.Log(_power[bin] + Tiny);
            if (probability > 0) entropy -= probability * Math.Log(probability);
            if (bin >= FftSize / 4) highPower += probability;
            cumulative += probability;
            if (rolloff == 0 && cumulative >= .85) rolloff = bin / (float)(Bins - 1);
            float magnitude = (float)(_magnitude[bin] * inverseMagnitude);
            if (!first) flux += Math.Max(0, magnitude - _previousMagnitude[bin]);
            _previousMagnitude[bin] = magnitude;
        }
        double spread = 0;
        for (int bin = 1; bin < Bins; bin++)
        {
            double distance = bin - center;
            spread += distance * distance * _power[bin] * inversePower;
        }
        int crossings = 0;
        for (int i = 1; i < FftSize; i++)
            if ((_signal[start + i] >= 0) != (_signal[start + i - 1] >= 0)) crossings++;
        Record(20, center / (Bins - 1));
        Record(21, Math.Sqrt(spread) / (Bins - 1));
        Record(22, rolloff);
        Record(23, Math.Exp(logPower / (Bins - 1)) / Math.Max(totalPower / (Bins - 1), Tiny));
        Record(24, entropy / Math.Log(Bins - 1));
        Record(25, flux);
        Record(26, highPower);
        Record(27, crossings / (double)(FftSize - 1));
        return flux;
    }

    private void AnalyzeMel()
    {
        double mean = 0;
        for (int band = 0; band < MelCount; band++)
        {
            double energy = 0;
            int offset = _melOffset[band];
            for (int bin = _melStart[band]; bin < _melEnd[band]; bin++)
                energy += _power[bin] * _melWeights[offset + bin - _melStart[band]];
            float value = (float)Math.Log(energy + Tiny);
            _mel[band] = value;
            mean += value;
        }
        mean /= MelCount;
        for (int coefficient = 0; coefficient < MfccCount; coefficient++)
        {
            double value = 0;
            for (int band = 0; band < MelCount; band++)
                value += (_mel[band] - mean) * _dct[coefficient * MelCount + band];
            Record(coefficient, value / 10);
        }
        for (int band = 0; band < 16; band++)
            Record(40 + band, ((_mel[band * 2] + _mel[band * 2 + 1]) * .5 - mean) / 6);
    }

    private void AnalyzeContrast()
    {
        for (int band = 0; band < 6; band++)
        {
            int begin = _contrastEdges[band];
            int count = _contrastEdges[band + 1] - begin;
            var values = _contrastScratch.AsSpan(0, count);
            for (int i = 0; i < count; i++) values[i] = _power[begin + i];
            SortAscending(values);
            int tail = Math.Max(1, count / 10);
            double low = 0;
            double high = 0;
            for (int i = 0; i < tail; i++)
            {
                low += values[i];
                high += values[count - i - 1];
            }
            Record(28 + band, Math.Clamp(10 * Math.Log10((high + Tiny) / (low + Tiny)) / 60, 0, 1));
        }
    }

    private void AnalyzePitch()
    {
        Array.Clear(_chroma);
        for (int bin = 1; bin < Bins; bin++)
        {
            float weight = _pitchScale[bin];
            if (weight == 0) continue;
            float value = _magnitude[bin] * weight;
            int left = _pitchLeft[bin];
            float fraction = _pitchFraction[bin];
            _chroma[left] += value * (1 - fraction);
            _chroma[(left + 1) % 12] += value * fraction;
        }
        double energy = 0;
        for (int i = 0; i < 12; i++) energy += _chroma[i] * _chroma[i];
        for (int interval = 1; interval <= 6; interval++)
        {
            double correlation = 0;
            for (int pitch = 0; pitch < 12; pitch++) correlation += _chroma[pitch] * _chroma[(pitch + interval) % 12];
            Record(33 + interval, correlation / Math.Max(energy, Tiny));
        }
    }

    private void AnalyzeRhythm(Span<float> output)
    {
        double mean = 0;
        for (int i = 0; i < FrameCount; i++) mean += _onsets[i];
        mean /= FrameCount;
        double variation = 0;
        for (int i = 0; i < FrameCount; i++)
        {
            double centered = _onsets[i] - mean;
            variation += centered * centered;
        }
        // 恒定纯音的浮点噪声不应被归一化成强节拍证据。
        if (mean < 1e-4 || variation / FrameCount < 1e-8) return;
        for (int band = 0; band < output.Length; band++)
        {
            double best = 0;
            for (int lag = _tempoLagStart[band]; lag <= _tempoLagEnd[band]; lag++)
            {
                double product = 0;
                double leftEnergy = 0;
                double rightEnergy = 0;
                for (int i = lag; i < FrameCount; i++)
                {
                    double left = _onsets[i] - mean;
                    double right = _onsets[i - lag] - mean;
                    product += left * right;
                    leftEnergy += left * left;
                    rightEnergy += right * right;
                }
                double divisor = Math.Sqrt(leftEnergy * rightEnergy);
                if (divisor > 1e-14) best = Math.Max(best, product / divisor);
            }
            output[band] = (float)Math.Clamp(best, 0, 1);
        }
    }

    private void AnalyzeDynamics(Span<float> output)
    {
        double sum = 0;
        float peak = 0;
        int silent = 0;
        for (int window = 0; window < _levelWindows.Length; window++)
        {
            double square = 0;
            for (int i = 0; i < 160; i++)
            {
                float value = _signal[window * 160 + i];
                square += value * value;
                peak = Math.Max(peak, Math.Abs(value));
            }
            sum += square;
            float rms = (float)Math.Sqrt(square / 160);
            if (rms < .00177828f) silent++;
            _levelWindows[window] = 20 * MathF.Log10(Math.Max(rms, 1e-8f));
        }
        SortAscending(_levelWindows);
        double overallRms = Math.Sqrt(sum / Length);
        output[0] = (float)Math.Clamp((20 * Math.Log10(Math.Max(overallRms, 1e-8)) + 80) / 80, 0, 1);
        output[1] = Math.Clamp((_levelWindows[900] - _levelWindows[100]) / 40, 0, 1);
        output[2] = (float)Math.Clamp(20 * Math.Log10(Math.Max(peak / Math.Max(overallRms, 1e-8), 1)) / 30, 0, 1);
        output[3] = silent / (float)_levelWindows.Length;
    }

    // 专用introsort不创建比较委托；只递归较小分区，深度有界，退化时回退堆排序。
    internal static void SortAscending(Span<float> values)
    {
        IntroSort(values, 2 * (BitOperations.Log2((uint)Math.Max(1, values.Length)) + 1));
    }

    private static void IntroSort(Span<float> values, int depth)
    {
        while (values.Length > 24)
        {
            if (depth-- == 0)
            {
                HeapSort(values);
                return;
            }
            float a = values[0], b = values[values.Length / 2], c = values[^1];
            float pivot = MathF.Max(MathF.Min(a, b), MathF.Min(MathF.Max(a, b), c));
            int lower = 0, cursor = 0, upper = values.Length - 1;
            // 三向分区使相同能量值不形成重复递归。
            while (cursor <= upper)
            {
                float value = values[cursor];
                if (value < pivot)
                {
                    values[cursor++] = values[lower];
                    values[lower++] = value;
                }
                else if (value > pivot)
                {
                    values[cursor] = values[upper];
                    values[upper--] = value;
                }
                else cursor++;
            }
            if (lower < values.Length - upper - 1)
            {
                IntroSort(values[..lower], depth);
                values = values[(upper + 1)..];
            }
            else
            {
                IntroSort(values[(upper + 1)..], depth);
                values = values[..lower];
            }
        }
        for (int i = 1; i < values.Length; i++)
        {
            float value = values[i];
            int position = i;
            while (position > 0 && values[position - 1] > value)
            {
                values[position] = values[position - 1];
                position--;
            }
            values[position] = value;
        }
    }

    private static void HeapSort(Span<float> values)
    {
        for (int root = values.Length / 2 - 1; root >= 0; root--) SiftDown(values, root, values.Length);
        for (int end = values.Length - 1; end > 0; end--)
        {
            float largest = values[0];
            values[0] = values[end];
            values[end] = largest;
            SiftDown(values, 0, end);
        }
    }

    private static void SiftDown(Span<float> values, int root, int count)
    {
        float value = values[root];
        while (root < count / 2)
        {
            int child = root * 2 + 1;
            if (child + 1 < count && values[child + 1] > values[child]) child++;
            if (value >= values[child]) break;
            values[root] = values[child];
            root = child;
        }
        values[root] = value;
    }

    private void Record(int index, double value)
    {
        if (!double.IsFinite(value)) value = 0;
        _sum[index] += value;
        _sumSquares[index] += value * value;
    }

    private void WriteMoments(int featureStart, int count, Span<float> output, int outputStart)
    {
        if (_activeFrames == 0) return;
        for (int i = 0; i < count; i++)
        {
            int source = featureStart + i;
            double mean = _sum[source] / _activeFrames;
            double variance = Math.Max(0, _sumSquares[source] / _activeFrames - mean * mean);
            output[outputStart + i] = (float)mean;
            output[outputStart + count + i] = (float)Math.Sqrt(variance);
        }
    }

    private static void WeightGroup(Span<float> values, float weight)
    {
        double square = 0;
        foreach (float value in values) square += value * value;
        if (square <= 1e-16) return;
        float scale = (float)Math.Sqrt(weight / square);
        for (int i = 0; i < values.Length; i++) values[i] *= scale;
    }

    private static void Normalize(Span<float> values)
    {
        double square = 0;
        foreach (float value in values) square += value * value;
        if (square <= 1e-16) return;
        float scale = (float)(1 / Math.Sqrt(square));
        for (int i = 0; i < values.Length; i++) values[i] *= scale;
    }

    private static float HertzToMel(float hertz) => 2595 * MathF.Log10(1 + hertz / 700);
    private static float MelToHertz(float mel) => 700 * (MathF.Pow(10, mel / 2595) - 1);
}
