using BassPlayerIpc.Shared;
using System.Numerics;

namespace AudioPlayer.Playback;

/// <summary>零附加延迟的 FIR；准备工作在后台线程执行，渲染器独占历史状态。</summary>
internal sealed class ConvolutionTier
{
    private readonly int Block, FftSize;
    private readonly double[][] _taps, _history;
    private readonly int _length, _channels;
    private readonly bool _useSimd;
    // 采用分数组频谱布局：连续实部频点之后存放连续虚部频点。
    // 避免在分区乘加的热点循环中重排向量通道。
    private readonly double[][][] _kernels, _spectra;
    private readonly Complex[][] _input, _work;
    private readonly double[][] _tail, _sum;
    private readonly int _partitions;
    private int _blockPosition, _spectrumPosition;
    private int _position;

    /// <summary>共享只读系数，发布后禁止修改任何层级的数组或元素；输入历史和 FFT 工作区属于单个滤波器。</summary>
    internal sealed record Coefficients(double[][] Taps, double[][][] Kernels, int Length, int Partitions, int Block);
    internal Coefficients Prepared { get; }

    internal bool HasSameCoefficients(ConvolutionTier other)
    {
        if (ReferenceEquals(Prepared, other.Prepared)) return true;
        // 缓存淘汰或预算外的 IR 在曲目边界仍可比较有界、不可变的系数。
        if (_channels != other._channels || _length != other._length || _partitions != other._partitions) return false;
        for (int ch = 0; ch < _channels; ch++)
        {
            if (!_taps[ch].AsSpan().SequenceEqual(other._taps[ch])) return false;
            for (int part = 0; part < _partitions; part++)
                if (!_kernels[ch][part].AsSpan().SequenceEqual(other._kernels[ch][part])) return false;
        }
        return true;
    }

    internal ConvolutionTier(ImpulseResponse impulse, int rate, int channels, bool useSimd = true)
        : this(Prepare(impulse, rate, channels), useSimd) { }

    internal ConvolutionTier(Coefficients prepared, bool useSimd = true)
    {
        Prepared = prepared;
        Block = prepared.Block; FftSize = Block * 2;
        _taps = prepared.Taps; _kernels = prepared.Kernels;
        _length = prepared.Length; _partitions = prepared.Partitions;
        _channels = _taps.Length;
        _useSimd = useSimd && Vector.IsHardwareAccelerated;
        Roots = CreateRoots();
        _history = new double[_channels][];
        _spectra = new double[_channels][][];
        _input = new Complex[_channels][]; _work = new Complex[_channels][];
        _tail = new double[_channels][]; _sum = new double[_channels][];
        for (int ch = 0; ch < _channels; ch++)
        {
            _history[ch] = new double[_length * 2];
            _spectra[ch] = new double[_partitions][];
            for (int p = 0; p < _partitions; p++) _spectra[ch][p] = new double[FftSize * 2];
            _input[ch] = new Complex[FftSize]; _work[ch] = new Complex[FftSize];
            _tail[ch] = new double[FftSize]; _sum[ch] = new double[FftSize * 2];
        }
    }

    internal static Coefficients Prepare(ImpulseResponse impulse, int rate, int channels, int block = 128, int? end = null, bool includeDirect = true, CancellationToken cancellation = default)
    {
        int Block = block, FftSize = block * 2;
        if (channels is < 1 or > 8) throw new InvalidDataException("Unsupported output channels.");
        impulse = impulse.AtRate(rate, cancellation);
        int count = Math.Min(impulse.Channels[0].Length, end ?? int.MaxValue);
        if (count > ImpulseResponse.MaxTaps) throw new InvalidDataException("Resampled IR exceeds 8192 taps.");
        int length = ((includeDirect ? Math.Min(count, Block) : Vector<double>.Count) + Vector<double>.Count - 1) / Vector<double>.Count * Vector<double>.Count;
        int partitions = (Math.Max(0, count - Block) + Block - 1) / Block;
        var taps = new double[channels][];
        var kernels = new double[channels][][];
        var work = new Complex[FftSize];
        for (int ch = 0; ch < channels; ch++)
        {
            cancellation.ThrowIfCancellationRequested();
            // 单声道映射到多个输出时共享只读系数；历史和 FFT 工作区仍逐声道独占。
            if (ch > 0 && impulse.Channels.Length == 1)
            {
                taps[ch] = taps[0];
                kernels[ch] = kernels[0];
                continue;
            }
            taps[ch] = new double[length];
            kernels[ch] = new double[partitions][];
            for (int p = 0; p < partitions; p++) kernels[ch][p] = new double[FftSize * 2];
            double[] source = impulse.Channels[impulse.Channels.Length == 1 ? 0 : ch];
            for (int i = 0; i < count; i++)
            {
                if (i < Block) { if (includeDirect) taps[ch][length - 1 - i] = source[i]; }
                else kernels[ch][(i - Block) / Block][i % Block] = source[i];
            }
            foreach (var kernel in kernels[ch])
            {
                cancellation.ThrowIfCancellationRequested();
                for (int i = 0; i < FftSize; i++) work[i] = new Complex(kernel[i], 0);
                ResponseMath.Fft(work, false);
                Pack(work, kernel);
            }
        }
        return new(taps, kernels, length, partitions, Block);
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

    // 首个分区采用直接卷积，使 FFT 尾部结果提前一个块就绪。
    // 因此不会增加延迟，并支持任意大小的渲染回调。
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
        for (int i = 0; i < source.Length; i++)
        {
            destination[i] = source[i].Real;
            destination[i + source.Length] = source[i].Imaginary;
        }
    }

    private readonly Complex[] Roots;
    private Complex[] CreateRoots()
    {
        var roots = new Complex[FftSize / 2];
        for (int i = 0; i < roots.Length; i++) roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / FftSize);
        return roots;
    }

    private void Transform(Complex[] data, bool inverse)
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
