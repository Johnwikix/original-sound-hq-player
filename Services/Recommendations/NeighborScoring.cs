using System;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>共用有界近邻参数与加权余弦聚合，不把未匹配样本当作负反馈。</summary>
internal static class NeighborScoring
{
    internal const int SampleLimit = 64;
    internal const int NeighborCount = 5;

    /// <summary>保存一个正向种子的相似度、偏好强度与稳定身份。</summary>
    internal readonly record struct Neighbor(int MusicId, double Similarity, double Weight);

    /// <summary>计算确定性加权蓄水池优先级，数值越小越优先。</summary>
    internal static double Priority(int musicId, double weight)
    {
        uint hash = unchecked((uint)musicId);
        hash = (hash ^ (hash >> 16)) * 0x7feb352du;
        hash = (hash ^ (hash >> 15)) * 0x846ca68bu;
        hash ^= hash >> 16;
        return -Math.Log((hash + 1.0) / (uint.MaxValue + 2.0)) / weight;
    }

    /// <summary>向调用方的栈缓冲插入最近邻；相同距离按曲目ID稳定排序。</summary>
    internal static void Offer(Span<Neighbor> nearest, ref int count, int musicId, double similarity, double weight)
    {
        if (!double.IsFinite(similarity) || similarity <= 0 || !double.IsFinite(weight) || weight <= 0) return;
        similarity = Math.Min(1, similarity);
        int slot = 0;
        while (slot < count && (nearest[slot].Similarity > similarity
            || (nearest[slot].Similarity == similarity && nearest[slot].MusicId < musicId))) slot++;
        if (slot >= nearest.Length) return;
        int end = Math.Min(count, nearest.Length - 1);
        for (int i = end; i > slot; i--) nearest[i] = nearest[i - 1];
        nearest[slot] = new(musicId, similarity, weight);
        count = Math.Min(nearest.Length, count + 1);
    }

    /// <summary>取全体种子的最大5个权重之和，提供候选间一致的缩放，保留正向支持数。</summary>
    internal static double Normalization(ReadOnlySpan<double> weights)
    {
        Span<double> largest = stackalloc double[NeighborCount];
        largest.Clear();
        foreach (double weight in weights)
        {
            int slot = 0;
            while (slot < largest.Length && largest[slot] >= weight) slot++;
            if (slot == largest.Length) continue;
            for (int i = largest.Length - 1; i > slot; i--) largest[i] = largest[i - 1];
            largest[slot] = weight;
        }
        double sum = 0;
        foreach (double weight in largest) sum += weight;
        return sum;
    }

    /// <summary>累计近邻相似度×偏好强度，按统一常数缩放，返回0–1分及主要解释种子。</summary>
    internal static double Aggregate(ReadOnlySpan<Neighbor> nearest, double normalization, out int strongestId)
    {
        double sum = 0, strongest = 0;
        strongestId = 0;
        foreach (var neighbor in nearest)
        {
            double contribution = neighbor.Similarity * neighbor.Weight;
            sum += contribution;
            if (contribution > strongest)
            {
                strongest = contribution;
                strongestId = neighbor.MusicId;
            }
        }
        return normalization > 0 ? Math.Clamp(sum / normalization, 0, 1) : 0;
    }
}
