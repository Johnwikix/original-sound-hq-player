using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.Recommendations;

/// <summary>使用可手算的向量与排序不变量验证近邻和融合边界。</summary>
internal static class NeighborAlgorithmRegression
{
    public static int Run(HomeRecommendationService service, DateTime now)
    {
        int checks = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new Exception(name);
            checks++;
            Console.WriteLine("PASS: " + name);
        }
        RecommendationFeatures Feature(int id, string artist, string genre, bool liked = false) =>
            new(new(id, [artist], "", "", genre, 2020, liked, true, 0, "Song " + id), [artist], [genre], "", "");
        var corpus = new[] { Feature(1, "A", "Rock", true), Feature(2, "B", "Rock"), Feature(3, "A", "Jazz") };
        var profile = ContentKnnProfile.Build(corpus, new Dictionary<int, HomeListening>(), default);
        // 独立三文档算例：公共词idf=1+ln(4/3)，稀有词idf=1+ln(2)。
        double common = 1 + Math.Log(4.0 / 3), rare = 1 + Math.Log(2);
        double expected = common / Math.Sqrt(2 * (common * common + rare * rare));
        Check(Math.Abs(profile.Score(corpus[1]).Score - expected) < 1e-12
            && Math.Abs(profile.Score(corpus[2]).Score - expected) < 1e-12,
            "TF-IDF cosine matches a hand-calculated sparse three-document example");
        Check(profile.Score(corpus[0]).Score == 0, "item-kNN excludes its own positive seed");
        Check(profile.Score(corpus[1]).Reason == RecommendationReason.Genre
            && profile.Score(corpus[2]).Reason == RecommendationReason.Artist,
            "metadata explanations identify a real shared term with a nearest seed");
        var support = corpus.Concat([Feature(4, "B", "Rock", true)]).ToArray();
        var supported = ContentKnnProfile.Build(support, new Dictionary<int, HomeListening>(), default);
        Check(supported.Score(support[1]).Score > supported.Score(support[2]).Score,
            "multiple positive neighbors retain support instead of cancelling it in a per-item weighted mean");
        var twoTastes = corpus.Concat([Feature(4, "C", "Metal", true), Feature(5, "C", "Metal")]).ToArray();
        var multi = ContentKnnProfile.Build(twoTastes, new Dictionary<int, HomeListening>(), default);
        Check(multi.Score(twoTastes[4]).Score > .49 && multi.Score(twoTastes[1]).Score > 0,
            "independent minority interests retain their own positive neighbors");

        RecommendationCandidate Candidate(int index, double score, RecommendationReason reason) =>
            new(corpus[index], score, 0, reason, "");
        var meta = new[] { Candidate(0, .9, RecommendationReason.Artist), Candidate(1, .2, RecommendationReason.Genre) };
        var audio = new[] { Candidate(1, .99, RecommendationReason.Audio), Candidate(2, .1, RecommendationReason.Audio) };
        var fused = RecommendationRankFusion.Fuse(meta, audio, [], default);
        var scaled = RecommendationRankFusion.Fuse(meta.Select(c => c with { Score = c.Score * c.Score }).ToArray(),
            audio.Select(c => c with { Score = c.Score / 100 }).ToArray(), [], default);
        Check(fused.SequenceEqual(scaled), "fusion depends on ranks rather than unrelated raw-score scales");
        Check(fused.Single(c => c.Features.Track.Id == 2).Score == 1
            && fused.Single(c => c.Features.Track.Id == 1).Score > fused.Single(c => c.Features.Track.Id == 3).Score,
            "independent source agreement increases reciprocal-rank support");
        var onlyMeta = RecommendationRankFusion.Fuse(meta, [], [], default);
        Check(onlyMeta.Count == 2 && onlyMeta[0].Score == 1 && double.IsFinite(onlyMeta[1].Score),
            "missing sources leave a valid standalone ranking without a zero-filled mean");
        var softened = RecommendationRankFusion.Fuse([meta[0] with { Features = meta[0].Features with { IsCollected = true } }, meta[1]], [], [], default);
        Check(softened.Single(c => c.Features.Track.Id == 1).Score == .1
            && softened.Single(c => c.Features.Track.Id == 2).Score > .9,
            "collected recommendations retain eligibility with a ninety-percent final-score reduction");
        Check(RecommendationRankFusion.Fuse([], [], [], default).Count == 0, "no sources produce no fabricated personalized scores");
        var distribution = new RecommendationRankFusion.SourceDistribution();
        distribution.Observe(.05);
        distribution.Observe(.95);
        Check(!distribution.Retain(.05) && distribution.Retain(.95),
            "weak observed matches below a source's own background do not cast duplicate fusion votes");
        var constant = new RecommendationRankFusion.SourceDistribution();
        constant.Observe(.5);
        constant.Observe(.5);
        Check(constant.Retain(.5), "a flat positive source remains available without an arbitrary absolute cutoff");

        var tracks = Enumerable.Range(1, 80).Select(id => new RecommendationTrack(id, ["A" + id % 10], "Album " + id,
            "A" + id % 10, id <= 40 ? "Rock" : "Jazz", 2020, id == 1, true, 0, "Song " + id)).ToArray();
        var clean = service.Build(tracks, [], now, 0, default);
        var repeatedTags = tracks.Select(track => track with { Artists = [track.Artists[0], " " + track.Artists[0].ToLowerInvariant() + " "] }).ToArray();
        Check(service.Build(repeatedTags, [], now, 0, default).Songs.SequenceEqual(clean.Songs),
            "duplicate or differently cased artist credits cannot inflate binary TF or document frequency");
        var unrelated = new RecommendationFeatures(corpus[1].Track with { Album = corpus[0].Track.Album, Year = corpus[0].Track.Year },
            ["B"], ["Jazz"], "", "");
        Check(profile.Score(unrelated).Score == 0,
            "matching release year or album cannot fabricate artist or genre neighbor evidence");
        var reordered = ContentKnnProfile.Build(twoTastes.Reverse().ToArray(), new Dictionary<int, HomeListening>(), default);
        Check(Math.Abs(reordered.Score(twoTastes[4]).Score - multi.Score(twoTastes[4]).Score) < 1e-12,
            "content neighbor fitting remains stable when snapshot order changes");
        var targetGroups = new HashSet<string>(["hit", "next"], StringComparer.Ordinal);
        var measured = OfflineEvaluation.ScoreRanking(["hit", "hit", "miss", "next"], targetGroups);
        double expectedNdcg = (1 + 1 / Math.Log2(5)) / (1 + 1 / Math.Log2(3));
        Check(measured.Hits == 2 && Math.Abs(measured.Ndcg - expectedNdcg) < 1e-12,
            "held-out duplicate groups cannot gain twice or compress NDCG positions");
        return checks;
    }
}
