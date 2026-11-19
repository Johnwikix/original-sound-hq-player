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

    // 每个启用的画布持有一组缓冲区。逐帧复用，延迟历史只引用已有的不可变 IPC 数据。
    private readonly float[] _levels = new float[PlaybackSpectrumOptions.MaximumCount];
    private readonly float[] _target = new float[PlaybackSpectrumOptions.MaximumCount];
    private readonly float[] _binPositions = new float[PlaybackSpectrumOptions.MaximumCount];
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

    public ReadOnlySpan<float> Levels => _levels.AsSpan(0, _count);
    public float Bass { get; private set; }
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
                _history.Enqueue(latest);
                _lastQueuedAt = latest.ReceivedAtMs;
            }
            while (_history?.TryPeek(out var next) == true && next.ReceivedAtMs <= due)
                _selected = _history.Dequeue();
        }

        if (_count != options.Count)
        {
            Array.Clear(_levels);
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

        float frames = Math.Clamp((float)elapsed.TotalSeconds * 60f, 0f, 6f);
        float attack = 1 - MathF.Pow(0.58f, frames);
        float release = 1 - MathF.Pow(0.80f, frames);
        HasEnergy = false;
        for (int i = 0; i < count; i++)
        {
            float delta = _target[i] - _levels[i];
            float level = _levels[i] + delta * (delta > 0 ? attack : release);
            _levels[i] = level > 0.001f ? level : 0;
            HasEnergy |= _levels[i] != 0;
        }
        Bass += (_targetBass - Bass) * (_targetBass > Bass ? attack : release);
    }

    private void ClearFrames()
    {
        _history?.Clear();
        _lastQueuedAt = -HistoryIntervalMs;
        _selected = default;
        _mappedSequence = 0;
        _targetBass = 0;
        Bass = 0;
        Array.Clear(_target);
        Array.Clear(_levels);
    }

    private void MapFrame(PlaybackSpectrumFrame frame)
    {
        var left = frame.Left.Span;
        var right = frame.Right.Span;
        int lastBin = Math.Min(left.Length, right.Length) - 1;
        if (_sampleRate != frame.SampleRate || _fftSize != frame.FftSize)
        {
            _sampleRate = frame.SampleRate;
            _fftSize = frame.FftSize;
            float upperHz = Math.Min(20000, _sampleRate * .5f);
            for (int i = 0; i < _count; i++)
            {
                // 镜像排列 L/R，低频位于中间。对数频率分布让低频保留足够的图形空间。
                float distance = MathF.Abs((i + .5f) * 2 / _count - 1);
                float hz = 20 * MathF.Pow(upperHz / 20, distance);
                _binPositions[i] = hz * _fftSize / _sampleRate;
            }
        }

        float sensitivity = _sensitivity * .01f;
        for (int i = 0; i < _count; i++)
        {
            float bin = Math.Clamp(_binPositions[i], 1f, lastBin);
            int first = (int)bin;
            int next = Math.Min(first + 1, lastBin);
            var channel = i * 2 < _count ? left : right;
            float magnitude = _count == 1
                ? (left[first] + right[first]) * .5f
                : channel[first] + (channel[next] - channel[first]) * (bin - first);
            _target[i] = Math.Clamp(MathF.Sqrt(magnitude * 3f) * sensitivity, 0, 3);
        }

        int bassBins = Math.Clamp(160 * _fftSize / _sampleRate, 1, lastBin);
        double power = 0;
        for (int i = 1; i <= bassBins; i++)
            power += (double)left[i] * left[i] + (double)right[i] * right[i];
        _targetBass = (float)Math.Min(1, 4 * Math.Sqrt(power * .5));
    }
}
