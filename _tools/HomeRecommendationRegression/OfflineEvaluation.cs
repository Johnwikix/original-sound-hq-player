using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using SQLite;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.Services.Recommendations;

/// <summary>只在本地快照副本上执行分组留出、时间留出及来源消融，不调用网络或修改用户库。</summary>
internal static class OfflineEvaluation
{
    internal sealed class MusicRow
    {
        public int Id { get; set; }
        public string Author { get; set; } = "";
        public string Title { get; set; } = "";
        public string Album { get; set; } = "";
        public string AlbumArtist { get; set; } = "";
        public string Genre { get; set; } = "";
        public int Year { get; set; }
        public bool IsFavorite { get; set; }
        public int PlayCount { get; set; }
    }
    internal sealed class Metric
    {
        public string Scenario { get; set; } = "";
        public string Model { get; set; } = "";
        public int Queries { get; set; }
        public int TargetOpportunities { get; set; }
        public int Hits20 { get; set; }
        public int Hits28 { get; set; }
        public double Recall20 { get; set; }
        public double Ndcg20 { get; set; }
        public double ArtistCount20 { get; set; }
        public double Fill20 { get; set; }
        public double DiscoveryCount { get; set; }
        public double DiscoveryDifferentGenreFraction { get; set; }
    }
    internal sealed class Report
    {
        public string Algorithm { get; set; } = "tfidf-cosine-knn-sum5-seeds64-centered-retrieval-rrf60-window200-collected0.1-heard-discovery0.5";
        public DateTime SnapshotUtc { get; set; }
        public int Tracks { get; set; }
        public int ExplicitPositiveGroups { get; set; }
        public int AnalysisRows { get; set; }
        public string LibrarySha256 { get; set; } = "";
        public string FeaturesSha256 { get; set; } = "";
        public List<Metric> Metrics { get; set; } = [];
        public List<ShelfCounts> FullSnapshotShelves { get; set; } = [];
        public List<string> Notes { get; set; } = [];
    }
    internal sealed record ShelfCounts(string Model, int Batch, int Primary, int Familiar, int Discovery, int Soundalikes, int Rediscover);
    internal sealed class Manifest
    {
        public DateTime NowUtc { get; set; }
        public string ArtistSplitSymbols { get; set; } = ", ; / 、 & feat. :";
        public Dictionary<string, string> Sha256 { get; set; } = [];
    }

    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(MusicRow))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(HomeListening))]
    internal static async Task RunAsync(string snapshotDirectory)
    {
        string root = Path.GetFullPath(snapshotDirectory);
        var manifest = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(root, "manifest.json")), EvaluationJson.Default.Manifest)!;
        string libraryPath = Path.Combine(root, "library.db");
        string featuresPath = Path.Combine(root, "features.db");
        string libraryHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(libraryPath))).ToLowerInvariant();
        string featuresHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(featuresPath))).ToLowerInvariant();
        if (libraryHash != manifest.Sha256["library.db"] || featuresHash != manifest.Sha256["features.db"])
            throw new InvalidDataException("Snapshot changed since backup; create a new snapshot directory.");
        string workingPath = Path.Combine(root, "evaluation-working.db");
        File.Copy(libraryPath, workingPath, overwrite: true);
        var db = new SQLiteAsyncConnection(workingPath);
        try
        {
            // 仅向派生工作副本附加并复制特征；原始快照及用户库均保持只读。
            await db.ExecuteAsync("ATTACH DATABASE ? AS evaluation_features", featuresPath);
            await db.ExecuteAsync("DROP TABLE IF EXISTS main.AnalysisRecord");
            await db.ExecuteAsync("CREATE TABLE main.AnalysisRecord AS SELECT * FROM evaluation_features.AnalysisRecord");
            await db.ExecuteAsync("CREATE UNIQUE INDEX evaluation_analysis_id ON AnalysisRecord(MusicId)");
            var repository = new AnalysisRepository(db);
            var playlist = (await db.QueryScalarsAsync<int>("SELECT DISTINCT pm.MusicId FROM PlayListMusic pm JOIN PlayList p ON p.Id=pm.PlayListId")).ToHashSet();
            var rows = await db.QueryAsync<MusicRow>("SELECT Id,Author,Title,Album,AlbumArtist,Genre,Year,IsFavorite,PlayCount FROM Music WHERE Id>0 ORDER BY Id");
            var splitters = manifest.ArtistSplitSymbols.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            var tracks = rows.Select(row => new RecommendationTrack(row.Id, SplitArtists(row.Author, splitters), row.Album,
                row.AlbumArtist, row.Genre, row.Year, row.IsFavorite, true, row.PlayCount, row.Title, playlist.Contains(row.Id))).ToArray();
            var history = await HomeListeningQuery.ReadAsync(db, manifest.NowUtc, .5, default);
            var positives = tracks.Where(track => track.IsFavorite || track.IsInPlaylist).Select(GroupKey).ToHashSet(StringComparer.Ordinal);
            var report = new Report
            {
                SnapshotUtc = manifest.NowUtc, Tracks = tracks.Length, ExplicitPositiveGroups = positives.Count,
                AnalysisRows = await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM AnalysisRecord WHERE Version=2 AND length(Data)=1536"),
                LibrarySha256 = libraryHash, FeaturesSha256 = featuresHash,
                Notes = ["Availability assumed true; real WebDAV/cache reachability is not simulated.",
                    "Five title-group folds; all held-out favorite, playlist, listening and cumulative-play evidence is removed for every namesake.",
                    "Three batches per fold. Hits/opportunities include repeated batch trials; Recall and NDCG are macro query means, not independent user observations.",
                    "Temporal experiment excludes all current explicit labels because their historical timestamps are unavailable; targets are future qualified listens absent from training history.",
                    "Community disabled for every model: no reproducible time-valid community snapshot is available.",
                    "Legacy baseline retains its original hard collection exclusion; the new production policy applies a 0.1 recommendation weight instead.",
                    "Main20 is the displayed Songs shelf; Primary28 includes carousel and side cards. No target labels enter model fitting."]
            };
            var fullAudio = await AudioPreferenceScorer.ScoreAsync(repository, tracks, history, default);
            var recent = new List<HomeRecommendation>();
            var coverage = new HashSet<int>();
            for (int batch = 0; batch < 12; batch++)
            {
                var result = new HomeRecommendationService().Build(tracks, history, manifest.NowUtc, batch, default, fullAudio,
                    recentRecommendations: recent);
                var counts = new ShelfCounts("current", batch, result.Songs.Count, result.Familiar.Count, result.Discovery.Count,
                    result.Soundalikes?.Count ?? 0, result.Rediscover?.Count ?? 0);
                var primary = result.Songs.Concat(result.Featured ?? []).Concat(result.Cards ?? []).Select(song => song.MusicId);
                coverage.UnionWith(primary);
                report.FullSnapshotShelves.Add(counts);
                Console.WriteLine($"full-snapshot | batch={batch} | primary={counts.Primary} | familiar={counts.Familiar} | discovery={counts.Discovery} | soundalikes={counts.Soundalikes} | rediscover={counts.Rediscover} | distinct primary to date={coverage.Count}");
                recent.Insert(0, result);
            }
            for (int fold = 0; fold < 5; fold++)
            {
                var heldOut = positives.Where(key => Fold(key) == fold).ToHashSet(StringComparer.Ordinal);
                if (heldOut.Count == 0) continue;
                var hiddenIds = tracks.Where(track => heldOut.Contains(GroupKey(track))).Select(track => track.Id).ToHashSet();
                var training = tracks.Select(track => hiddenIds.Contains(track.Id)
                    ? track with { IsFavorite = false, IsInPlaylist = false, PlayCount = 0 } : track).ToArray();
                await EvaluateAsync("explicit-group-holdout", training, history.Where(row => !hiddenIds.Contains(row.MusicId)).ToArray(),
                    heldOut, manifest.NowUtc, repository, report);
            }

            DateTime cutoff = manifest.NowUtc.AddDays(-14);
            var before = await HomeListeningQuery.ReadAsync(db, cutoff, .5, default);
            var beforeMap = before.ToDictionary(row => row.MusicId);
            var knownGroups = tracks.Where(track => beforeMap.ContainsKey(track.Id)).Select(GroupKey).ToHashSet(StringComparer.Ordinal);
            var futureIds = (await db.QueryScalarsAsync<int>("SELECT DISTINCT MusicId FROM PlaybackHistory WHERE StartedAt>? AND StartedAt<=? "
                + "AND TotalDurationMs>0 AND DurationPlayedMs>=TotalDurationMs*.5", cutoff.Ticks, manifest.NowUtc.Ticks)).ToHashSet();
            var futureTargets = tracks.Where(track => futureIds.Contains(track.Id) && !knownGroups.Contains(GroupKey(track)))
                .Select(GroupKey).ToHashSet(StringComparer.Ordinal);
            if (futureTargets.Count > 0 && before.Count > 0)
            {
                var training = tracks.Select(track => track with { IsFavorite = false, IsInPlaylist = false,
                    PlayCount = beforeMap.GetValueOrDefault(track.Id)?.Count ?? 0 }).ToArray();
                await EvaluateAsync("temporal-history-only-14d", training, before, futureTargets, cutoff, repository, report);
            }
            else report.Notes.Add("Temporal experiment skipped: no nonempty training history and new future target cohort.");
            foreach (var metric in report.Metrics)
            {
                metric.Recall20 /= metric.Queries;
                metric.Ndcg20 /= metric.Queries;
                metric.ArtistCount20 /= metric.Queries;
                metric.Fill20 /= metric.Queries;
                metric.DiscoveryCount /= metric.Queries;
                metric.DiscoveryDifferentGenreFraction /= metric.Queries;
            }
            string resultPath = Path.Combine(root, "evaluation.json");
            File.WriteAllText(resultPath, JsonSerializer.Serialize(report, EvaluationJson.Default.Report).ReplaceLineEndings("\r\n"));
            foreach (var metric in report.Metrics)
                Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{metric.Scenario} | {metric.Model} | q={metric.Queries} | hits20={metric.Hits20}/{metric.TargetOpportunities} | recall20={metric.Recall20:F4} | ndcg20={metric.Ndcg20:F4} | hits28={metric.Hits28} | artists={metric.ArtistCount20:F1} | fill={metric.Fill20:F2}"));
        }
        finally { await db.CloseAsync(); }
    }

    private static async Task EvaluateAsync(string scenario, RecommendationTrack[] tracks, IReadOnlyList<HomeListening> history,
        HashSet<string> targets, DateTime now, AnalysisRepository repository, Report report)
    {
        var legacyAudio = await LegacyAudioPreferenceScorer.ScoreAsync(repository, tracks, history, default);
        var audio = await AudioPreferenceScorer.ScoreAsync(repository, tracks, history, default);
        var service = new HomeRecommendationService();
        var legacy = new LegacyHomeRecommendationService();
        var byId = tracks.ToDictionary(track => track.Id);
        var weights = history.ToDictionary(row => row.MusicId, row => row.Weight);
        var collectedNames = tracks.Where(track => track.IsFavorite || track.IsInPlaylist).Select(track => track.Title.Trim())
            .Where(title => title.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        double CollectionWeight(RecommendationTrack track) => track.IsFavorite || track.IsInPlaylist
            || (!string.IsNullOrWhiteSpace(track.Title) && collectedNames.Contains(track.Title.Trim())) ? .1 : 1;
        var eligible = tracks;
        for (int batch = 0; batch < 3; batch++)
        {
            var random = eligible.OrderByDescending(track => StableHash(GroupKey(track) + batch) * CollectionWeight(track)).Take(28).Select(track => new RecommendedSong(track.Id, RecommendationReason.Explore)).ToArray();
            var frequency = eligible.OrderByDescending(track => RecommendationEvidence.Strength(false, weights.GetValueOrDefault(track.Id)) * CollectionWeight(track)).ThenBy(track => track.Id)
                .Take(28).Select(track => new RecommendedSong(track.Id, RecommendationReason.Familiar)).ToArray();
            Record(scenario, "random", new(random.Take(20).ToArray(), [], [], true, random.Skip(20).ToArray()), targets, byId, report);
            Record(scenario, "recent-frequency", new(frequency.Take(20).ToArray(), [], [], false, frequency.Skip(20).ToArray()), targets, byId, report);
            Record(scenario, "legacy-f1455f32", legacy.Build(tracks, history, now, batch, default, legacyAudio), targets, byId, report);
            Record(scenario, "tfidf-knn-only", service.Build(tracks, history, now, batch, default, null, null, RecommendationSources.Metadata), targets, byId, report);
            Record(scenario, "audio-knn-only", service.Build(tracks, history, now, batch, default, audio, null, RecommendationSources.Audio), targets, byId, report);
            Record(scenario, "tfidf-audio-rrf", service.Build(tracks, history, now, batch, default, audio), targets, byId, report);
        }
    }

    private static void Record(string scenario, string model, HomeRecommendation result, HashSet<string> targets,
        Dictionary<int, RecommendationTrack> tracks, Report report)
    {
        var metric = report.Metrics.Find(row => row.Scenario == scenario && row.Model == model);
        if (metric is null) { metric = new() { Scenario = scenario, Model = model }; report.Metrics.Add(metric); }
        var primary = (result.Featured ?? []).Concat(result.Cards ?? []).Concat(result.Songs).ToArray();
        var all = primary.Concat(result.Familiar).Concat(result.Discovery).Concat(result.Rediscover ?? []).Concat(result.Soundalikes ?? []).ToArray();
        if (all.Select(song => song.MusicId).Distinct().Count() != all.Length) throw new Exception("Duplicate page recording in " + model);
        var mainKeys = result.Songs.Take(20).Select(song => GroupKey(tracks[song.MusicId])).ToArray();
        var (hits, ndcg) = ScoreRanking(mainKeys, targets);
        metric.Queries++;
        metric.TargetOpportunities += targets.Count;
        metric.Hits20 += hits;
        metric.Hits28 += primary.Select(song => GroupKey(tracks[song.MusicId])).Distinct().Count(targets.Contains);
        metric.Recall20 += hits / (double)targets.Count;
        metric.Ndcg20 += ndcg;
        metric.ArtistCount20 += result.Songs.SelectMany(song => tracks[song.MusicId].Artists).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        metric.Fill20 += result.Songs.Count / 20.0;
        metric.DiscoveryCount += result.Discovery.Count;
        var mainGenres = result.Songs.SelectMany(song => HomeRecommendationService.SplitGenres(tracks[song.MusicId].Genre)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        metric.DiscoveryDifferentGenreFraction += result.Discovery.Count > 0 ? result.Discovery.Count(song =>
            HomeRecommendationService.SplitGenres(tracks[song.MusicId].Genre).Any(genre => !mainGenres.Contains(genre))) / (double)result.Discovery.Count : 0;
    }

    /// <summary>按展示位置计算同名组增益，重复组不重复得分，也不压缩后续排名。</summary>
    internal static (int Hits, double Ndcg) ScoreRanking(IReadOnlyList<string> ranking, HashSet<string> targets)
    {
        var gained = new HashSet<string>(StringComparer.Ordinal);
        double dcg = 0, ideal = 0;
        for (int i = 0; i < Math.Min(20, ranking.Count); i++)
            if (targets.Contains(ranking[i]) && gained.Add(ranking[i])) dcg += 1 / Math.Log2(i + 2);
        for (int i = 0; i < Math.Min(20, targets.Count); i++) ideal += 1 / Math.Log2(i + 2);
        return (gained.Count, ideal > 0 ? dcg / ideal : 0);
    }

    private static string[] SplitArtists(string author, string[] splitters)
    {
        IEnumerable<string> parts = [author];
        foreach (string splitter in splitters) parts = parts.SelectMany(part => part.Split(splitter, StringSplitOptions.None)).ToArray();
        return parts.Select(part => part.Trim()).Where(part => part.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static string GroupKey(RecommendationTrack track) => string.IsNullOrWhiteSpace(track.Title)
        ? "ID:" + track.Id : "TITLE:" + track.Title.Trim().ToUpperInvariant();
    private static int Fold(string key) => (int)(StableHash(key) % 5);
    private static uint StableHash(string key)
    {
        uint hash = 2166136261;
        foreach (char value in key) hash = unchecked((hash ^ value) * 16777619);
        return hash;
    }
}

[JsonSerializable(typeof(OfflineEvaluation.Manifest))]
[JsonSerializable(typeof(OfflineEvaluation.Report))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class EvaluationJson : JsonSerializerContext { }
