using System;
using System.Collections.Generic;
using System.Threading;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>用二值TF、平滑IDF、L2余弦及最多64个正向种子计算内容item-kNN。</summary>
/// <remarks>仅保存词频和种子倒排项，不构建全曲库两两相似度矩阵。</remarks>
internal sealed class ContentKnnProfile
{
    private sealed class Term(double idf)
    {
        internal readonly double Idf = idf;
        internal List<Posting>? Postings;
    }
    private readonly record struct Posting(int Seed, double Value);
    private readonly record struct Seed(RecommendationFeatures Features, double Weight, double Priority);
    internal readonly record struct Match(double Score, RecommendationReason Reason, string Detail, bool IsAvailable = false);
    private readonly Dictionary<string, Term> _artists = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Term> _genres = new(StringComparer.OrdinalIgnoreCase);
    private readonly Seed[] _seeds = new Seed[NeighborScoring.SampleLimit];
    private int _seedCount;
    private double _normalization;

    private ContentKnnProfile() { }

    /// <summary>显式拟合本批次TF-IDF及确定性加权种子，调用方保持输入快照稳定。</summary>
    internal static ContentKnnProfile Build(IReadOnlyList<RecommendationFeatures> features,
        IReadOnlyDictionary<int, HomeListening> history, CancellationToken token)
    {
        var profile = new ContentKnnProfile();
        foreach (var feature in features)
        {
            token.ThrowIfCancellationRequested();
            if (feature.Artists.Length + feature.Genres.Length == 0) continue;
            double weight = RecommendationEvidence.Strength(feature.Track, history.GetValueOrDefault(feature.Track.Id));
            if (weight <= 0) continue;
            double priority = NeighborScoring.Priority(feature.Track.Id, weight);
            int slot = profile._seedCount;
            if (slot == NeighborScoring.SampleLimit)
            {
                slot = 0;
                for (int i = 1; i < profile._seedCount; i++)
                    if (profile._seeds[i].Priority > profile._seeds[slot].Priority) slot = i;
                if (priority >= profile._seeds[slot].Priority) continue;
            }
            else profile._seedCount++;
            profile._seeds[slot] = new(feature, weight, priority);
        }
        if (profile._seedCount == 0) return profile;
        var artistCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var feature in features)
        {
            token.ThrowIfCancellationRequested();
            Count(artistCounts, feature.Artists);
            Count(genreCounts, feature.Genres);
        }
        BuildTerms(profile._artists, artistCounts, features.Count);
        BuildTerms(profile._genres, genreCounts, features.Count);
        for (int i = 0; i < profile._seedCount; i++)
        {
            var feature = profile._seeds[i].Features;
            double norm = profile.Norm(feature);
            AddPostings(profile._artists, feature.Artists, i, norm);
            AddPostings(profile._genres, feature.Genres, i, norm);
        }
        Span<double> weights = stackalloc double[NeighborScoring.SampleLimit];
        for (int i = 0; i < profile._seedCount; i++) weights[i] = profile._seeds[i].Weight;
        profile._normalization = NeighborScoring.Normalization(weights[..profile._seedCount]);
        return profile;
    }

    /// <summary>计算候选与最近5个正向种子的加权余弦；.NET 10+栈缓冲不逃逸到异步或UI。</summary>
    internal Match Score(in RecommendationFeatures feature)
    {
        if (_seedCount == 0) return default;
        double norm = Norm(feature);
        if (norm <= 0) return default;
        Span<double> dots = stackalloc double[NeighborScoring.SampleLimit];
        dots.Clear();
        Accumulate(_artists, feature.Artists, dots);
        Accumulate(_genres, feature.Genres, dots);
        Span<NeighborScoring.Neighbor> nearest = stackalloc NeighborScoring.Neighbor[NeighborScoring.NeighborCount];
        int count = 0;
        for (int i = 0; i < _seedCount; i++)
        {
            if (_seeds[i].Features.Track.Id == feature.Track.Id) continue;
            NeighborScoring.Offer(nearest, ref count, _seeds[i].Features.Track.Id, dots[i] / norm, _seeds[i].Weight);
        }
        double score = NeighborScoring.Aggregate(nearest[..count], _normalization, out int strongestId);
        if (score <= 0) return new(0, RecommendationReason.Explore, "", true);
        int strongestSeed = 0;
        while (_seeds[strongestSeed].Features.Track.Id != strongestId) strongestSeed++;
        double bestTerm = 0;
        string detail = "";
        RecommendationReason reason = RecommendationReason.Explore;
        Explain(_artists, feature.Artists, strongestSeed, RecommendationReason.Artist, ref bestTerm, ref reason, ref detail);
        Explain(_genres, feature.Genres, strongestSeed, RecommendationReason.Genre, ref bestTerm, ref reason, ref detail);
        return new(score, reason, detail, true);
    }

    private double Norm(in RecommendationFeatures feature)
    {
        double squares = SquaredNorm(_artists, feature.Artists) + SquaredNorm(_genres, feature.Genres);
        return Math.Sqrt(squares);
    }

    private static double SquaredNorm(Dictionary<string, Term> terms, string[] values)
    {
        double sum = 0;
        foreach (string value in values)
            if (terms.TryGetValue(value, out var term)) sum += term.Idf * term.Idf;
        return sum;
    }

    private static void Count(Dictionary<string, int> counts, string[] values)
    {
        foreach (string value in values) counts[value] = counts.GetValueOrDefault(value) + 1;
    }

    private static void BuildTerms(Dictionary<string, Term> terms, Dictionary<string, int> counts, int documents)
    {
        foreach (var pair in counts) terms.Add(pair.Key, new(1 + Math.Log((documents + 1.0) / (pair.Value + 1.0))));
    }

    private static void AddPostings(Dictionary<string, Term> terms, string[] values, int seed, double norm)
    {
        if (norm <= 0) return;
        foreach (string value in values)
        {
            var term = terms[value];
            (term.Postings ??= []).Add(new(seed, term.Idf / norm));
        }
    }

    private static void Accumulate(Dictionary<string, Term> terms, string[] values, Span<double> dots)
    {
        foreach (string value in values)
            if (terms.TryGetValue(value, out var term) && term.Postings is { } postings)
                foreach (var posting in postings) dots[posting.Seed] += term.Idf * posting.Value;
    }

    private static void Explain(Dictionary<string, Term> terms, string[] values, int seed, RecommendationReason kind,
        ref double best, ref RecommendationReason reason, ref string detail)
    {
        foreach (string value in values)
            if (terms.TryGetValue(value, out var term) && term.Idf > best && term.Postings is { } postings)
                foreach (var posting in postings)
                    if (posting.Seed == seed)
                    {
                        best = term.Idf;
                        reason = kind;
                        detail = value;
                        break;
                    }
    }
}
