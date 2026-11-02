using System;
using WinUIMusicPlayer.AudioAnalysis;

namespace WinUIMusicPlayer.Services.AudioAnalysis;

/// <summary>最多64个正向样本、8个兴趣中心；保留多种喜欢的音色/节奏，不保存全库向量。</summary>
public sealed class AudioTasteProfile
{
    private const int Dimensions = 128;
    private const int SampleLimit = 64;
    private const int CenterLimit = 8;
    private readonly float[] _samples = new float[SampleLimit * Dimensions];
    private readonly double[] _weights = new double[SampleLimit];
    private readonly double[] _priorities = new double[SampleLimit];
    private readonly float[] _centers = new float[CenterLimit * Dimensions];
    private readonly float[] _support = new float[CenterLimit];
    private int _sampleCount;
    private int _centerCount;

    /// <summary>确定性加权采样；同一曲目只由调用方提交一次，向量已完成曲库校准与L2归一化。</summary>
    public void Add(int musicId, ReadOnlySpan<float> vector, double weight)
    {
        if (!double.IsFinite(weight) || weight <= 0 || FeatureMath.Dot(vector, vector) < 1e-8f) return;
        foreach (float value in vector) if (!float.IsFinite(value)) return;
        uint hash = unchecked((uint)musicId);
        hash = (hash ^ (hash >> 16)) * 0x7feb352du;
        hash = (hash ^ (hash >> 15)) * 0x846ca68bu;
        hash ^= hash >> 16;
        double priority = -Math.Log((hash + 1.0) / (uint.MaxValue + 2.0)) / weight;
        int slot = _sampleCount;
        if (_sampleCount == SampleLimit)
        {
            slot = 0;
            for (int i = 1; i < _sampleCount; i++) if (_priorities[i] > _priorities[slot]) slot = i;
            if (priority >= _priorities[slot]) return;
        }
        else _sampleCount++;
        vector.CopyTo(_samples.AsSpan(slot * Dimensions, Dimensions));
        _weights[slot] = weight;
        _priorities[slot] = priority;
    }

    /// <summary>从差异明显的正向样本初始化中心，再进行三轮有界聚类。</summary>
    public bool Fit()
    {
        _centerCount = 0;
        if (_sampleCount == 0) return false;
        int first = 0;
        for (int i = 1; i < _sampleCount; i++) if (_weights[i] > _weights[first]) first = i;
        _samples.AsSpan(first * Dimensions, Dimensions).CopyTo(_centers);
        _centerCount = 1;
        while (_centerCount < Math.Min(CenterLimit, _sampleCount))
        {
            int next = -1;
            double separation = .10;
            for (int sample = 0; sample < _sampleCount; sample++)
            {
                var vector = _samples.AsSpan(sample * Dimensions, Dimensions);
                float nearest = -1;
                for (int center = 0; center < _centerCount; center++)
                    nearest = Math.Max(nearest, FeatureMath.Dot(vector, _centers.AsSpan(center * Dimensions, Dimensions)));
                double distance = (1 - Math.Clamp(nearest, -1, 1)) * Math.Sqrt(_weights[sample] / _weights[first]);
                if (distance > separation)
                {
                    separation = distance;
                    next = sample;
                }
            }
            if (next < 0) break;
            _samples.AsSpan(next * Dimensions, Dimensions).CopyTo(_centers.AsSpan(_centerCount++ * Dimensions, Dimensions));
        }
        var sums = new float[CenterLimit * Dimensions];
        var mass = new double[CenterLimit];
        for (int iteration = 0; iteration < 3; iteration++)
        {
            Array.Clear(sums);
            Array.Clear(mass);
            for (int sample = 0; sample < _sampleCount; sample++)
            {
                var vector = _samples.AsSpan(sample * Dimensions, Dimensions);
                int closest = 0;
                float best = float.NegativeInfinity;
                for (int center = 0; center < _centerCount; center++)
                {
                    float match = FeatureMath.Dot(vector, _centers.AsSpan(center * Dimensions, Dimensions));
                    if (match > best)
                    {
                        best = match;
                        closest = center;
                    }
                }
                double weight = _weights[sample];
                mass[closest] += weight;
                var sum = sums.AsSpan(closest * Dimensions, Dimensions);
                for (int i = 0; i < Dimensions; i++) sum[i] += (float)(vector[i] * weight);
            }
            for (int center = 0; center < _centerCount; center++)
            {
                if (mass[center] == 0) continue;
                var sum = sums.AsSpan(center * Dimensions, Dimensions);
                FeatureMath.Normalize(sum);
                sum.CopyTo(_centers.AsSpan(center * Dimensions, Dimensions));
            }
        }
        double maximum = 0;
        for (int center = 0; center < _centerCount; center++) maximum = Math.Max(maximum, mass[center]);
        for (int center = 0; center < _centerCount; center++)
            _support[center] = mass[center] > 0 ? (float)(.80 + .20 * Math.Sqrt(mass[center] / maximum)) : 0;
        return true;
    }

    /// <summary>取最相近兴趣中心，适度考虑支持度；未匹配到兴趣不被解释为负反馈。</summary>
    public float Score(ReadOnlySpan<float> vector)
    {
        float best = 0;
        for (int center = 0; center < _centerCount; center++)
        {
            float match = FeatureMath.Dot(vector, _centers.AsSpan(center * Dimensions, Dimensions));
            if (float.IsFinite(match)) best = Math.Max(best, match * _support[center]);
        }
        return Math.Clamp(best, 0, 1);
    }
}
