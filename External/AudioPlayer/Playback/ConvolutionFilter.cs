using BassPlayerIpc.Shared;
using System.Numerics;

namespace AudioPlayer.Playback;

/// <summary>Zero-latency FIR. Preparation is off-thread; history belongs exclusively to the renderer.</summary>
internal sealed class ConvolutionFilter
{
    private const int Block = 128, FftSize = Block * 2;
    private readonly double[][] _taps, _history;
    private readonly int _length, _channels;
    private readonly bool _useSimd;
    // Structure-of-arrays spectra: contiguous real bins followed by imaginary bins.
    // This avoids lane shuffles in the partition multiply-accumulate hot loop.
    private readonly double[][][] _kernels, _spectra;
    private readonly Complex[][] _input, _work;
    private readonly double[][] _tail, _sum;
    private readonly int _partitions;
    private int _blockPosition, _spectrumPosition;
    private int _position;

    internal ConvolutionFilter(ImpulseResponse impulse, int rate, int channels, bool useSimd = true)
    {
        if (channels is < 1 or > 2) throw new InvalidDataException("Unsupported output channels.");
        impulse = impulse.AtRate(rate);
        _channels = channels;
        _useSimd = useSimd && Vector.IsHardwareAccelerated;
        int count = checked((int)Math.Ceiling(impulse.Channels[0].Length * (double)rate / impulse.SampleRate));
        if (count > ImpulseResponse.MaxTaps) throw new InvalidDataException("Resampled IR exceeds 8192 taps.");
        _length = (Math.Min(count, Block) + Vector<double>.Count - 1) / Vector<double>.Count * Vector<double>.Count;
        _partitions = (Math.Max(0, count - Block) + Block - 1) / Block;
        _taps = new double[channels][];
        _history = new double[channels][];
        _kernels = new double[channels][][];
        _spectra = new double[channels][][];
        _input = new Complex[channels][];
        _work = new Complex[channels][];
        _tail = new double[channels][];
        _sum = new double[channels][];
        for (int ch = 0; ch < channels; ch++)
        {
            _taps[ch] = new double[_length];
            _history[ch] = new double[_length * 2];
            _kernels[ch] = new double[_partitions][];
            _spectra[ch] = new double[_partitions][];
            _input[ch] = new Complex[FftSize];
            _work[ch] = new Complex[FftSize];
            _tail[ch] = new double[FftSize];
            _sum[ch] = new double[FftSize * 2];
            for (int p = 0; p < _partitions; p++)
            {
                _kernels[ch][p] = new double[FftSize * 2];
                _spectra[ch][p] = new double[FftSize * 2];
            }
            double[] source = impulse.Channels[Math.Min(ch, impulse.Channels.Length - 1)];
            for (int i = 0; i < count; i++)
            {
                double value = source[i];
                if (i < Block) _taps[ch][_length - 1 - i] = value;
                else _kernels[ch][(i - Block) / Block][i % Block] = value;
            }
            foreach (var kernel in _kernels[ch])
            {
                var work = _work[ch];
                for (int i = 0; i < FftSize; i++) work[i] = new Complex(kernel[i], 0);
                Transform(work, false);
                Pack(work, kernel);
            }
        }
    }

    internal void Reset()
    {
        foreach (var history in _history) Array.Clear(history);
        foreach (var channel in _spectra) foreach (var spectrum in channel) Array.Clear(spectrum);
        foreach (var input in _input) Array.Clear(input);
        foreach (var tail in _tail) Array.Clear(tail);
        _position = 0;
        _blockPosition = _spectrumPosition = 0;
    }

    internal void Process(Span<double> samples, int frames, double gain, ref double mix, ref double currentGain, double step, bool enabled = true)
    {
        for (int frame = 0; frame < frames; frame++)
        {
            mix = enabled ? Math.Min(1, mix + step) : Math.Max(0, mix - step);
            currentGain += Math.Clamp(gain - currentGain, -step, step);
            for (int ch = 0; ch < _channels; ch++)
            {
                int index = frame * _channels + ch;
                double input = double.IsFinite(samples[index]) ? samples[index] : 0;
                if (_partitions > 0) _input[ch][_blockPosition] = new Complex(input, 0);
                double[] history = _history[ch], taps = _taps[ch];
                history[_position] = history[_position + _length] = input;
                int start = _position + 1;
                double direct = 0;
                if (_useSimd)
                {
                    var sum = Vector<double>.Zero;
                    for (int tap = 0; tap < _length; tap += Vector<double>.Count)
                        sum += new Vector<double>(history, start + tap) * new Vector<double>(taps, tap);
                    direct = Vector.Sum(sum);
                }
                else for (int tap = 0; tap < _length; tap++) direct += history[start + tap] * taps[tap];
                double output = (direct + _tail[ch][_blockPosition]) * currentGain;
                samples[index] = double.IsFinite(output) ? input + (output - input) * mix : 0;
            }
            if (++_position == _length) _position = 0;
            if (++_blockPosition == Block)
            {
                _blockPosition = 0;
                if (_partitions > 0) ProcessTail();
            }
        }
    }

    // The first partition is direct, so the FFT tail is ready one block before it is needed.
    // This retains zero added latency and supports arbitrary render callback sizes.
    private void ProcessTail()
    {
        for (int ch = 0; ch < _channels; ch++)
        {
            var spectrum = _spectra[ch][_spectrumPosition];
            var work = _work[ch];
            _input[ch].CopyTo(work, 0);
            Transform(work, false);
            Pack(work, spectrum);
            var sum = _sum[ch];
            Array.Clear(sum);
            int index = _spectrumPosition;
            for (int p = 0; p < _partitions; p++)
            {
                var input = _spectra[ch][index];
                var kernel = _kernels[ch][p];
                if (_useSimd) for (int bin = 0; bin < FftSize; bin += Vector<double>.Count)
                {
                    var xr = new Vector<double>(input, bin);
                    var xi = new Vector<double>(input, bin + FftSize);
                    var hr = new Vector<double>(kernel, bin);
                    var hi = new Vector<double>(kernel, bin + FftSize);
                    (new Vector<double>(sum, bin) + xr * hr - xi * hi).CopyTo(sum, bin);
                    (new Vector<double>(sum, bin + FftSize) + xr * hi + xi * hr).CopyTo(sum, bin + FftSize);
                }
                else for (int bin = 0; bin < FftSize; bin++)
                {
                    sum[bin] += input[bin] * kernel[bin] - input[bin + FftSize] * kernel[bin + FftSize];
                    sum[bin + FftSize] += input[bin] * kernel[bin + FftSize] + input[bin + FftSize] * kernel[bin];
                }
                if (--index < 0) index = _partitions - 1;
            }
            for (int i = 0; i < FftSize; i++) work[i] = new Complex(sum[i], sum[i + FftSize]);
            Transform(work, true);
            var tail = _tail[ch];
            for (int i = 0; i < Block; i++)
            {
                tail[i] = tail[i + Block] + work[i].Real;
                tail[i + Block] = work[i + Block].Real;
            }
        }
        if (++_spectrumPosition == _partitions) _spectrumPosition = 0;
    }

    private static void Pack(Complex[] source, double[] destination)
    {
        for (int i = 0; i < FftSize; i++)
        {
            destination[i] = source[i].Real;
            destination[i + FftSize] = source[i].Imaginary;
        }
    }

    private static readonly Complex[] Roots = CreateRoots();
    private static Complex[] CreateRoots()
    {
        var roots = new Complex[FftSize / 2];
        for (int i = 0; i < roots.Length; i++) roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / FftSize);
        return roots;
    }

    private static void Transform(Complex[] data, bool inverse)
    {
        for (int i = 1, j = 0; i < FftSize; i++)
        {
            int bit = FftSize >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) (data[i], data[j]) = (data[j], data[i]);
        }
        for (int size = 2; size <= FftSize; size <<= 1)
            for (int start = 0; start < FftSize; start += size)
                for (int i = 0; i < size / 2; i++)
                {
                    Complex root = Roots[i * FftSize / size];
                    if (inverse) root = Complex.Conjugate(root);
                    Complex even = data[start + i], odd = data[start + i + size / 2] * root;
                    data[start + i] = even + odd;
                    data[start + i + size / 2] = even - odd;
                }
        if (inverse) for (int i = 0; i < FftSize; i++) data[i] /= FftSize;
    }
}
