#nullable enable
using System;
using System.Collections.Generic;
using AnimatedWin2dControls.Controls;

namespace AnimatedWin2dControls.Renderer;

internal sealed class PlaybackSpectrumSignal
{
    private const int SnapshotTimeoutMs = 150;
    private const int HistoryIntervalMs = 16;
    private const int HistoryCapacity = PlaybackSpectrumOptions.MaximumDelayMs / HistoryIntervalMs + 3;
    private const float SmoothingAt60Hz = .90f;
    private const float SilentLevel = .001f;
    private const float BreathingRange = .05f;

    private readonly record struct Band(float FirstBin, float LastBin, float RightWeight);

    // 每个启用的画布持有一组缓冲区。逐帧复用，延迟历史只引用已有的不可变 IPC 数据。
    private readonly float[] _levels = new float[PlaybackSpectrumOptions.MaximumCount];
    private readonly float[] _target = new float[PlaybackSpectrumOptions.MaximumCount];
    private readonly float[] _temporal = new float[PlaybackSpectrumOptions.MaximumCount];
    private readonly Band[] _bands = new Band[PlaybackSpectrumOptions.MaximumCount];
    private Queue<PlaybackSpectrumFrame>? _history;
    private PlaybackSpectrumFrame _selected;
    private long _epoch = -1;
    private long _lastQueuedAt = -HistoryIntervalMs;
    private long _mappedSequence;
    private int _delayMs;
    private int _sampleRate;
    private int _fftSize;
    private int _count;
    private int _sensitivity;
    private float _targetBass;
    private float _bass;

    public ReadOnlySpan<float> Levels => _levels.AsSpan(0, _count);
    public float BreathingScale { get; private set; } = 1;
    public bool HasEnergy { get; private set; }

    public void Update(PlaybackSpectrumFrame latest, long nowMs, TimeSpan elapsed, PlaybackSpectrumOptions options)
    {
        if (latest.Sequence != 0 && (_epoch != latest.Epoch || !latest.IsAvailable))
        {
            if (_epoch != latest.Epoch || _selected.IsAvailable || _history?.Count > 0)
                ClearFrames();
            _epoch = latest.Epoch;
        }

        if (_delayMs != options.DelayMs)
        {
            ClearFrames();
            _delayMs = options.DelayMs;
            if (_delayMs == 0) _history = null;
        }

        if (_delayMs == 0)
        {
            _selected = latest;
        }
        else
        {
            long due = nowMs - _delayMs;
            if (latest.IsAvailable && nowMs - latest.ReceivedAtMs <= SnapshotTimeoutMs &&
                latest.ReceivedAtMs - _lastQueuedAt >= HistoryIntervalMs)
            {
                _history ??= new Queue<PlaybackSpectrumFrame>(HistoryCapacity);
                if (_history.Count == HistoryCapacity) _history.Dequeue();
                _history.Enqueue(latest);
                _lastQueuedAt = latest.ReceivedAtMs;
            }
            while (_history?.TryPeek(out var next) == true && next.ReceivedAtMs <= due)
                _selected = _history.Dequeue();
        }

        if (_count != options.Count)
        {
            Array.Clear(_temporal);
            _sampleRate = 0;
        }
        int count = options.Count;
        bool remap = _count != count || _sensitivity != options.Sensitivity;
        _count = count;
        _sensitivity = options.Sensitivity;
        bool available = _selected.IsAvailable && nowMs - _selected.ReceivedAtMs <= _delayMs + SnapshotTimeoutMs;
        if (available && (remap || _mappedSequence != _selected.Sequence))
        {
            MapFrame(_selected);
            _mappedSequence = _selected.Sequence;
        }
        else if (!available && (_mappedSequence != 0 || remap))
        {
            Array.Clear(_target);
            _targetBass = 0;
            _mappedSequence = 0;
        }

        // 以极光之环 60 Hz 时的保留系数为基准，按实际帧间隔更新。
        float frames = Math.Clamp((float)elapsed.TotalSeconds * 60f, 0f, 6f);
        float follow = 1 - MathF.Pow(SmoothingAt60Hz, frames);
        for (int i = 0; i < count; i++)
        {
            float level = float.Lerp(_temporal[i], _target[i], follow);
            _temporal[i] = _target[i] == 0 && level < SilentLevel ? 0 : level;
        }

        // 时间平滑与频段加权各读一组缓冲区，环绕封面时首尾也是相邻频段。
        bool wrap = options.Placement == SpectrumPlacement.AroundArtwork;
        HasEnergy = false;
        for (int i = 0; i < count; i++)
        {
            int previous = i == 0 ? (wrap ? count - 1 : 0) : i - 1;
            int next = i + 1 == count ? (wrap ? 0 : count - 1) : i + 1;
            float level = (_temporal[previous] + _temporal[next]) * .25f + _temporal[i] * .5f;
            _levels[i] = level;
            HasEnergy |= level >= SilentLevel;
        }

        _bass = float.Lerp(_bass, _targetBass, follow);
        BreathingScale = options.Breathing
            ? 1 + _bass * BreathingRange * (options.BreathingIntensity * .01f)
            : 1;
    }

    private void ClearFrames()
    {
        _history?.Clear();
        _lastQueuedAt = -HistoryIntervalMs;
        _selected = default;
        _mappedSequence = 0;
        _targetBass = 0;
        _bass = 0;
        Array.Clear(_target);
        Array.Clear(_temporal);
    }

    private void MapFrame(PlaybackSpectrumFrame frame)
    {
        var left = frame.Left.Span;
        var right = frame.Right.Span;
        int lastBin = Math.Min(left.Length, right.Length) - 1;
        if (_sampleRate != frame.SampleRate || _fftSize != frame.FftSize)
            ConfigureBands(frame.SampleRate, frame.FftSize);

        float sensitivity = _sensitivity * .01f;
        for (int i = 0; i < _count; i++)
        {
            Band band = _bands[i];
            float first = Math.Clamp(band.FirstBin, 1, lastBin);
            float last = Math.Clamp(band.LastBin, first, lastBin);
            float leftPeak = band.RightWeight < 1 ? Peak(left, first, last) : 0;
            float rightPeak = band.RightWeight > 0 ? Peak(right, first, last) : 0;
            float magnitude = float.Lerp(leftPeak, rightPeak, band.RightWeight);
            _target[i] = Math.Clamp(MathF.Sqrt(magnitude * 3f) * sensitivity, 0, 3);
        }

        // 沿用极光之环的低频平均能量。双声道 IPC 只传幅度，使用左右幅度的均值。
        int bassBins = Math.Min(250 * _fftSize / _sampleRate, lastBin);
        float bassSum = 0;
        for (int i = 1; i <= bassBins; i++)
            bassSum += MathF.Min(1, (left[i] + right[i]) * 8f);
        _targetBass = bassBins == 0 ? 0 : bassSum / bassBins;
    }

    private void ConfigureBands(int sampleRate, int fftSize)
    {
        _sampleRate = sampleRate;
        _fftSize = fftSize;
        float upperHz = MathF.Min(16000, sampleRate * .45f);
        float lowerHz = MathF.Min(40, upperHz);
        float ratio = upperHz / lowerHz;
        float firstBin = lowerHz * fftSize / sampleRate;
        if (_count == 1)
        {
            _bands[0] = new(firstBin, upperHz * fftSize / sampleRate, .5f);
            return;
        }

        int leftCount = (_count + 1) / 2;
        for (int i = 0; i < _count; i++)
        {
            bool isLeft = i < leftCount;
            int sideCount = isLeft ? leftCount : _count - leftCount;
            int frequencyIndex = isLeft ? leftCount - 1 - i : i - leftCount;
            float low = firstBin * MathF.Pow(ratio, frequencyIndex / (float)sideCount);
            float high = firstBin * MathF.Pow(ratio, (frequencyIndex + 1f) / sideCount);

            // 两端的声道接缝向双声道平均值过渡，中间保留各自的声道。
            float distance = MathF.Min(frequencyIndex + .5f, sideCount - frequencyIndex - .5f);
            float blend = Math.Clamp(distance / MathF.Max(4f, sideCount * .12f), 0, 1);
            blend *= blend * (3 - 2 * blend);
            _bands[i] = new(low, high, .5f + (isLeft ? -.5f : .5f) * blend);
        }
    }

    private static float Peak(ReadOnlySpan<float> bins, float first, float last)
    {
        float peak = MathF.Max(Sample(bins, first), Sample(bins, last));
        for (int i = (int)first + 1; i <= (int)last; i++)
            peak = MathF.Max(peak, bins[i]);
        return peak;
    }

    private static float Sample(ReadOnlySpan<float> bins, float position)
    {
        int index = (int)position;
        return float.Lerp(bins[index], bins[Math.Min(index + 1, bins.Length - 1)], position - index);
    }
}
