using System;

namespace WinUIMusicPlayer.AudioAnalysis;

/// <summary>逐帧 STFT 与时间列功率平均；缓冲有界，声道先各自变换再合并功率，避免反相信号抵消。</summary>
public sealed class SpectrogramAccumulator
{
    private readonly int _size, _hop, _bins, _width, _channels, _rate;
    private readonly double _duration;
    private readonly float[] _left, _right, _window, _real, _imaginary, _cos, _sin, _leftPower, _rightPower;
    private readonly int[] _reverse, _counts;
    private readonly float _scale;
    private int _filled;
    private long _samples, _start;

    public SpectrogramAccumulator(int sampleRate, int channels, double duration, int fftSize)
    {
        if (!SpectrogramData.ValidFftSize(fftSize) || sampleRate <= 0 || channels is < 1 or > 2
            || !double.IsFinite(duration) || duration <= 0 || duration > 86400) throw new ArgumentException("Invalid spectrum dimensions.");
        _size = fftSize;
        _hop = fftSize / 2;
        _bins = fftSize / 2 + 1;
        _width = Math.Clamp((int)Math.Min(SpectrogramData.MaxWidth, Math.Ceiling(duration * sampleRate / _hop)), 1, SpectrogramData.MaxWidth);
        _channels = channels;
        _rate = sampleRate;
        _duration = duration;
        _left = new float[_size];
        _right = channels == 2 ? new float[_size] : [];
        _window = new float[_size];
        _real = new float[_size];
        _imaginary = new float[_size];
        _reverse = new int[_size];
        _cos = new float[_size / 2];
        _sin = new float[_size / 2];
        _counts = new int[_width];
        _leftPower = new float[_width * _bins];
        _rightPower = channels == 2 ? new float[_width * _bins] : [];
        float sum = 0;
        int bits = System.Numerics.BitOperations.Log2((uint)_size);
        for (int i = 0; i < _size; i++)
        {
            _window[i] = .5f - .5f * MathF.Cos(2 * MathF.PI * i / (_size - 1));
            sum += _window[i];
            int source = i, reversed = 0;
            for (int bit = 0; bit < bits; bit++)
            {
                reversed = (reversed << 1) | (source & 1);
                source >>= 1;
            }
            _reverse[i] = reversed;
        }
        _scale = 2 / sum;
        for (int i = 0; i < _cos.Length; i++)
        {
            _cos[i] = MathF.Cos(-2 * MathF.PI * i / _size);
            _sin[i] = MathF.Sin(-2 * MathF.PI * i / _size);
        }
    }

    public long Samples => _samples;
    public void Add(ReadOnlySpan<float> samples)
    {
        if (samples.Length % _channels != 0) throw new ArgumentException("Incomplete PCM frame.");
        for (int frame = 0; frame < samples.Length / _channels; frame++)
        {
            _left[_filled] = samples[frame * _channels];
            if (_channels == 2) _right[_filled] = samples[frame * 2 + 1];
            _filled++;
            _samples++;
            if (_filled == _size) TransformWindow();
        }
    }

    private void TransformWindow()
    {
        int column = Math.Clamp((int)(_start / (_duration * _rate) * _width), 0, _width - 1);
        Transform(_left, _leftPower.AsSpan(column * _bins, _bins));
        if (_channels == 2) Transform(_right, _rightPower.AsSpan(column * _bins, _bins));
        _counts[column]++;
        _left.AsSpan(_hop).CopyTo(_left);
        if (_channels == 2) _right.AsSpan(_hop).CopyTo(_right);
        _filled -= _hop;
        _start += _hop;
    }

    private void Transform(float[] signal, Span<float> target)
    {
        for (int i = 0; i < _size; i++) _real[i] = signal[_reverse[i]] * _window[_reverse[i]];
        Array.Clear(_imaginary);
        for (int size = 2; size <= _size; size <<= 1)
        {
            int half = size / 2, step = _size / size;
            for (int start = 0; start < _size; start += size)
            for (int j = 0; j < half; j++)
            {
                int a = start + j, b = a + half, twiddle = j * step;
                float real = _cos[twiddle] * _real[b] - _sin[twiddle] * _imaginary[b];
                float imaginary = _cos[twiddle] * _imaginary[b] + _sin[twiddle] * _real[b];
                _real[b] = _real[a] - real;
                _imaginary[b] = _imaginary[a] - imaginary;
                _real[a] += real;
                _imaginary[a] += imaginary;
            }
        }
        for (int bin = 0; bin < _bins; bin++)
        {
            float scale = bin == 0 || bin == _bins - 1 ? _scale / 2 : _scale;
            float power = (_real[bin] * _real[bin] + _imaginary[bin] * _imaginary[bin]) * scale * scale;
            if (float.IsFinite(power)) target[bin] += power;
        }
    }

    public SpectrogramData Finish(int sourceChannels)
    {
        if (_samples == 0) throw new InvalidOperationException("No audio samples.");
        while (_start < _samples)
        {
            _left.AsSpan(Math.Max(0, _filled)).Clear();
            if (_channels == 2) _right.AsSpan(Math.Max(0, _filled)).Clear();
            TransformWindow();
        }
        var combined = new byte[_width * _bins];
        var left = _channels == 2 ? new byte[combined.Length] : [];
        var right = _channels == 2 ? new byte[combined.Length] : [];
        for (int x = 0; x < _width; x++)
        {
            int count = Math.Max(1, _counts[x]);
            for (int y = 0; y < _bins; y++)
            {
                int index = x * _bins + y;
                float l = _leftPower[index] / count;
                float r = _channels == 2 ? _rightPower[index] / count : l;
                combined[index] = Encode((l + r) * .5f);
                if (_channels == 2)
                {
                    left[index] = Encode(l);
                    right[index] = Encode(r);
                }
            }
        }
        return new(_rate, sourceChannels, _size, _width, _duration, combined, left, right);
    }

    private static byte Encode(float power) => (byte)Math.Clamp((int)MathF.Round(10 * MathF.Log10(MathF.Max(power, 1e-16f)) + 160), 0, 160);
}
