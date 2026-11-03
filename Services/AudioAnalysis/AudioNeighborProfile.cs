using System;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Services.Recommendations;

namespace WinUIMusicPlayer.Services.AudioAnalysis;

/// <summary>保存最多64个已校准正向样本，以最近5个样本的加权余弦保留不同听音兴趣。</summary>
public sealed class AudioNeighborProfile
{
    private const int Dimensions = 128;
    private readonly float[] _samples = new float[NeighborScoring.SampleLimit * Dimensions];
    private readonly double[] _weights = new double[NeighborScoring.SampleLimit];
    private readonly double[] _priorities = new double[NeighborScoring.SampleLimit];
    private readonly int[] _ids = new int[NeighborScoring.SampleLimit];
    private int _count;
    private double _normalization;

    /// <summary>获取是否包含有效正向样本。</summary>
    public bool HasSamples => _count > 0;

    /// <summary>以确定性加权蓄水池收集128维、已L2归一化的正向样本；不保留调用方内存。</summary>
    /// <param name="musicId">一首已去重正向曲目的标识。</param>
    /// <param name="vector">一个已校准并归一化的128维向量。</param>
    /// <param name="weight">一个有限且大于零的偏好强度。</param>
    public void Add(int musicId, ReadOnlySpan<float> vector, double weight)
    {
        if (vector.Length != Dimensions || musicId <= 0 || !double.IsFinite(weight) || weight <= 0) return;
        foreach (float value in vector) if (!float.IsFinite(value)) return;
        if (FeatureMath.Dot(vector, vector) < 1e-8f) return;
        double priority = NeighborScoring.Priority(musicId, weight);
        int slot = _count;
        if (_count == NeighborScoring.SampleLimit)
        {
            slot = 0;
            for (int i = 1; i < _count; i++) if (_priorities[i] > _priorities[slot]) slot = i;
            if (priority >= _priorities[slot]) return;
        }
        else _count++;
        vector.CopyTo(_samples.AsSpan(slot * Dimensions, Dimensions));
        _weights[slot] = weight;
        _priorities[slot] = priority;
        _ids[slot] = musicId;
        _normalization = NeighborScoring.Normalization(_weights.AsSpan(0, _count));
    }

    /// <summary>计算候选与正向近邻的加权余弦，排除自身；.NET 10+热路径只用栈缓冲和SIMD点积。</summary>
    /// <param name="musicId">一个待评分曲目的标识。</param>
    /// <param name="vector">一个已校准并归一化的128维候选向量。</param>
    /// <returns>一个按共享种子权重缩放的0–1近邻支持分，不是喜欢概率。</returns>
    public float Score(int musicId, ReadOnlySpan<float> vector)
    {
        if (vector.Length != Dimensions) return 0;
        Span<NeighborScoring.Neighbor> nearest = stackalloc NeighborScoring.Neighbor[NeighborScoring.NeighborCount];
        int count = 0;
        for (int i = 0; i < _count; i++)
        {
            if (_ids[i] == musicId) continue;
            float similarity = FeatureMath.Dot(vector, _samples.AsSpan(i * Dimensions, Dimensions));
            NeighborScoring.Offer(nearest, ref count, _ids[i], similarity, _weights[i]);
        }
        return (float)NeighborScoring.Aggregate(nearest[..count], _normalization, out _);
    }
}
