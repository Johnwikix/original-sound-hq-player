using System;
using System.Collections.Generic;
using System.Threading;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>对有界来源列表执行等权RRF，缺失来源不参与该曲目的名次贡献。</summary>
internal static class RecommendationRankFusion
{
    internal const int RankConstant = 60;
    internal const int SourceWindow = 200;

    /// <summary>对来源内已知分数做均值中心化召回，未知项不进入统计。</summary>
    internal struct SourceDistribution
    {
        private int _count;
        private double _sum, _min, _max;
        internal void Observe(double value)
        {
            if (_count == 0)
            {
                _min = value;
                _max = value;
            }
            else
            {
                _min = Math.Min(_min, value);
                _max = Math.Max(_max, value);
            }
            _count++;
            _sum += value;
        }
        internal readonly bool HasContrast => _count > 1 && _max - _min > 1e-8;
        internal readonly bool Retain(double value) => value > 0
            && (_count <= 1 || _max - _min <= 1e-8 || value > _sum / _count);
    }
    private readonly record struct Accumulator(RecommendationCandidate Candidate, double Sum, double BestContribution);

    /// <summary>合并来源名次并按批次最大RRF分归一化，供后续多样性重排；不是概率。</summary>
    internal static List<RecommendationCandidate> Fuse(IReadOnlyList<RecommendationCandidate> metadata,
        IReadOnlyList<RecommendationCandidate> audio, IReadOnlyList<RecommendationCandidate> community, CancellationToken token)
    {
        var scores = new Dictionary<int, Accumulator>();
        Add(scores, metadata, token);
        Add(scores, audio, token);
        Add(scores, community, token);
        double max = 0;
        foreach (var score in scores.Values) max = Math.Max(max, score.Sum);
        var result = new List<RecommendationCandidate>(scores.Count);
        foreach (var value in scores.Values)
        {
            double score = value.Sum / max * value.Candidate.Features.RecommendationWeight;
            result.Add(value.Candidate with { Score = score, Affinity = score });
        }
        return result;
    }

    private static void Add(Dictionary<int, Accumulator> target, IReadOnlyList<RecommendationCandidate> source, CancellationToken token)
    {
        for (int i = 0; i < source.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            var candidate = source[i];
            double contribution = 1.0 / (RankConstant + i + 1);
            if (target.TryGetValue(candidate.Features.Track.Id, out var current))
            {
                var explanation = contribution > current.BestContribution ? candidate : current.Candidate;
                target[candidate.Features.Track.Id] = new(explanation, current.Sum + contribution, Math.Max(current.BestContribution, contribution));
            }
            else target.Add(candidate.Features.Track.Id, new(candidate, contribution, contribution));
        }
    }
}
