using System;
using System.Collections.Generic;
using System.Threading;
using WinUIMusicPlayer.Model.Recommendations;
using Features = WinUIMusicPlayer.Services.Recommendations.RecommendationFeatures;
using Candidate = WinUIMusicPlayer.Services.Recommendations.RecommendationCandidate;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>仅使用本地快照生成推荐，以有限候选重排控制艺术家、专辑和流派多样性。</summary>
public sealed class HomeRecommendationService
{
    private const int CandidateLimit = 200;
    private const int PageSize = 20;
    private const int FeaturedLimit = 5;
    private const int CardLimit = 3;
    private const double HeardDiscoveryWeight = .5;
    private const double RelevanceTolerance = .12;
    private static readonly string[] UnknownNames =
    [
        "未知", "未知艺术家", "未知专辑", "未知流派", "Unknown", "Unknown Artist", "Unknown Album", "Unknown Genre",
        "不明", "不明なアーティスト", "不明なアルバム", "不明なジャンル", "Неизвестный исполнитель", "Неизвестный артист",
        "Неизвестный альбом", "Неизвестный жанр", "Unbekannter Künstler", "Unbekanntes Album", "Unbekanntes Genre",
        "Artista desconocido", "Álbum desconocido", "Género desconocido", "Bilinmiyor", "Bilinmeyen Sanatçı", "Bilinmeyen Albüm", "Bilinmeyen Tür"
    ];

    /// <summary>从固定快照生成三个栏目；同一天、同一批次和输入产生相同结果。</summary>
    public HomeRecommendation Build(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history,
        DateTime nowUtc, int batch, CancellationToken token,
        IReadOnlyDictionary<int, float>? audio = null, CommunitySignals? community = null)
        => Build(tracks, history, nowUtc, batch, token, audio, community, RecommendationSources.All);

    /// <summary>用指定召回源生成同一栏目策略的快照，允许在不改变候选资格的情况下做消融对照。</summary>
    internal HomeRecommendation Build(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history,
        DateTime nowUtc, int batch, CancellationToken token, IReadOnlyDictionary<int, float>? audio,
        CommunitySignals? community, RecommendationSources sources)
    {
        token.ThrowIfCancellationRequested();
        var listening = new Dictionary<int, HomeListening>(history.Count);
        foreach (var row in history) listening[row.MusicId] = row;
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var genreSupport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var features = new List<Features>(tracks.Count);
        var genreCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var albumKeys = new Dictionary<(string Artist, string Album), string>();
        var collectedTitles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool hasPreference = false;
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            if (track.Id <= 0) continue;
            var artistNames = FilterArtists(track.Artists);
            string rawGenre = track.Genre ?? "";
            if (!genreCache.TryGetValue(rawGenre, out var genreNames))
            {
                genreNames = SplitGenres(rawGenre);
                genreCache.Add(rawGenre, genreNames);
            }
            string albumKey = "";
            if (IsKnown(track.Album.AsSpan()))
            {
                var albumIdentity = (IsKnown(track.AlbumArtist.AsSpan()) ? track.AlbumArtist.Trim() :
                    artistNames.Length > 0 ? artistNames[0] : "", track.Album.Trim());
                if (!albumKeys.TryGetValue(albumIdentity, out var cachedAlbumKey))
                {
                    cachedAlbumKey = string.Concat(albumIdentity.Item1, "\u001f", albumIdentity.Item2);
                    albumKeys.Add(albumIdentity, cachedAlbumKey);
                }
                albumKey = cachedAlbumKey;
            }
            string recordingKey = artistNames.Length > 0 && IsKnown(track.Title.AsSpan())
                ? CommunitySignals.TrackKey(string.Join(";", artistNames), track.Title) : "";
            var item = new Features(track, artistNames, genreNames, albumKey, recordingKey);
            features.Add(item);
            if ((track.IsFavorite || track.IsInPlaylist) && !string.IsNullOrWhiteSpace(track.Title))
                collectedTitles.Add(track.Title.Trim());
            Increment(genreSupport, genreNames);
            listening.TryGetValue(track.Id, out var listened);
            double strength = RecommendationEvidence.Strength(track, listened);
            if (strength <= 0) continue;
            hasPreference = true;
            Add(genres, genreNames, strength);
        }

        for (int i = 0; i < features.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            features[i] = features[i] with { IsCollected = IsCollected(features[i].Track, collectedTitles) };
        }
        double genreMax = ProfileMaximum(genres, genreSupport);
        var contentProfile = (sources & RecommendationSources.Metadata) != 0 ? ContentKnnProfile.Build(features, listening, token) : null;
        var metadataRanked = new PriorityQueue<Candidate, (double, double, int)>();
        var audioRanked = new PriorityQueue<Candidate, (double, double, int)>();
        var communityRanked = new PriorityQueue<Candidate, (double, double, int)>();
        var fallback = new PriorityQueue<Candidate, (double, double, int)>();
        var explore = new PriorityQueue<Candidate, (double, double, int)>();
        var familiar = new PriorityQueue<Candidate, (double, double, int)>();
        var metadataDistribution = new RecommendationRankFusion.SourceDistribution();
        var audioDistribution = new RecommendationRankFusion.SourceDistribution();
        var communityDistribution = new RecommendationRankFusion.SourceDistribution();
        var genreRepresentatives = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        int candidateCount = 0;
        uint seed = unchecked((uint)(nowUtc.Date.Ticks / TimeSpan.TicksPerDay) * 16777619u ^ (uint)batch * 2246822519u);
        foreach (var item in features)
        {
            token.ThrowIfCancellationRequested();
            var track = item.Track;
            if (!track.IsAvailable) continue;
            listening.TryGetValue(track.Id, out var listened);
            double jitter = RandomScore(track.Id, seed);
            // 常听是回听入口；收藏和歌单的展示降权不影响真实收听排序。
            if (listened is { Count: > 0 })
            {
                double repeat = RecommendationEvidence.Strength(false, listened) / 3;
                Offer(familiar, new(item, .95 * repeat + .05 * jitter, 0, RecommendationReason.Familiar, ""));
            }
            candidateCount++;
            double g = Match(genres, genreSupport, item.Genres, genreMax);
            var content = contentProfile?.Score(item) ?? default;
            double acoustic = (sources & RecommendationSources.Audio) != 0 ? ReadSignal(audio, track.Id) : 0;
            double remote = 0;
            if ((sources & RecommendationSources.Community) != 0 && community is not null)
                foreach (string artist in item.Artists)
                {
                    remote = Math.Max(remote, ReadSignal(community.Artists, artist));
                    if (track.Title.Length > 0) remote = Math.Max(remote, ReadSignal(community.Tracks, CommunitySignals.TrackKey(artist, track.Title)));
                }
            if (content.Score > 0) Offer(metadataRanked, new(item, content.Score, 0, content.Reason, content.Detail, jitter), RecommendationRankFusion.SourceWindow, true);
            if (acoustic > 0) Offer(audioRanked, new(item, acoustic, 0, RecommendationReason.Audio, "", jitter), RecommendationRankFusion.SourceWindow, true);
            if (remote > 0) Offer(communityRanked, new(item, remote, 0, RecommendationReason.Community, "", jitter), RecommendationRankFusion.SourceWindow, true);
            double recommendationWeight = item.RecommendationWeight;
            if (content.IsAvailable) metadataDistribution.Observe(content.Score * recommendationWeight);
            if ((sources & RecommendationSources.Audio) != 0 && audio is not null
                && audio.TryGetValue(track.Id, out float observedAudio) && float.IsFinite(observedAudio) && observedAudio >= 0)
                audioDistribution.Observe(acoustic * recommendationWeight);
            if (remote > 0) communityDistribution.Observe(remote * recommendationWeight);
            Offer(fallback, new(item, jitter * recommendationWeight, 0, RecommendationReason.Explore, "", jitter * recommendationWeight));
            // 跨流派探索不设置主推荐的亲和度下限；陌生流派本来就可能没有匹配证据。
            double discoveryScore = 1 - g;
            // 换流派也允许回听；播放记录只降低权重，不能把听过的曲库整栏筛空。
            double discoveryWeight = recommendationWeight * (track.PlayCount > 0 || listened is { Count: > 0 } ? HeardDiscoveryWeight : 1);
            var discovery = new Candidate(item, discoveryScore * discoveryWeight, 0, RecommendationReason.Explore, "", jitter * discoveryWeight);
            if (item.Genres.Length > 0
                && genreSupport.Count > 1 && (genreMax <= 0 || g < 1 - 1e-6))
            {
                Offer(explore, discovery);
                foreach (string genre in item.Genres)
                    if (!genreRepresentatives.TryGetValue(genre, out var previous) || discovery.Score > previous.Score
                        || (discovery.Score == previous.Score && discovery.TieBreaker > previous.TieBreaker))
                        genreRepresentatives[genre] = discovery;
            }
        }

        var representatives = new PriorityQueue<Candidate, (double, double, int)>();
        foreach (var item in genreRepresentatives.Values) Offer(representatives, item);
        var pool = RecommendationRankFusion.Fuse(DrainSource(metadataRanked, metadataDistribution),
            DrainSource(audioRanked, audioDistribution), DrainSource(communityRanked, communityDistribution), token);
        hasPreference |= pool.Count > 0;
        var fallbackPool = Drain(fallback);
        if (pool.Count > 0)
            for (int i = 0; i < fallbackPool.Count; i++) fallbackPool[i] = fallbackPool[i] with { Score = 0 };
        Merge(pool, fallbackPool);
        var exploration = Drain(explore);
        Merge(exploration, Drain(representatives));
        var seen = new HashSet<int>();
        var recordings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 全页共享排除集合：常听、精选、主栏、探索分别分配独立曲目。
        var familiarPool = Drain(familiar);
        var familiarSongs = Select(familiarPool, Math.Min(PageSize, Math.Max(1, candidateCount / 4)), token, seen, recordings, false);
        int remaining = candidateCount;
        foreach (var item in familiarPool)
            if (seen.Contains(item.Features.Track.Id)) remaining--;
        int mainReserve = Math.Min(PageSize, Math.Max(0, remaining));
        // 保留主栏和常规精选配额；有剩余候选时至少为探索预留一项，避免被其他栏取空。
        int discoveryQuota = Math.Min(PageSize, Math.Max(remaining - mainReserve - FeaturedLimit - CardLimit,
            remaining > mainReserve ? 1 : 0));
        var discoverySongs = Select(exploration, discoveryQuota, token, seen, recordings, false);
        remaining -= discoverySongs.Count;
        int featuredCount = Math.Min(FeaturedLimit, Math.Max(0, remaining - mainReserve));
        var featured = Select(pool, featuredCount, token, seen, recordings, hasPreference);
        int cardCount = Math.Min(CardLimit, Math.Max(0, remaining - featured.Count - mainReserve));
        var cards = Select(pool, cardCount, token, seen, recordings, hasPreference);
        var songs = Select(pool, PageSize, token, seen, recordings, hasPreference);
        // 源文件数不能代表独立录音数；存在未收录歌曲时，至少保留一个主栏入口。
        if (songs.Count == 0)
        {
            if (featured.Count > 0)
            {
                songs.Add(featured[0]);
                featured.RemoveAt(0);
            }
            else
            {
                foreach (var item in familiarPool)
                {
                    if (IsCollected(item.Features.Track, collectedTitles)) continue;
                    int index = -1;
                    for (int i = 0; i < familiarSongs.Count; i++)
                        if (familiarSongs[i].MusicId == item.Features.Track.Id) { index = i; break; }
                    if (index < 0) continue;
                    songs.Add(familiarSongs[index]);
                    familiarSongs.RemoveAt(index);
                    break;
                }
            }
        }
        return new(songs, familiarSongs, discoverySongs, !hasPreference, featured, cards);
    }

    private static bool IsCollected(RecommendationTrack track, HashSet<string> collectedTitles) =>
        track.IsFavorite || track.IsInPlaylist
        || (!string.IsNullOrWhiteSpace(track.Title) && collectedTitles.Contains(track.Title.Trim()));

    private static List<RecommendedSong> Select(List<Candidate> pool, int count, CancellationToken token, HashSet<int> selected, HashSet<string> recordings, bool personalized)
    {
        var result = new List<RecommendedSong>(count);
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (result.Count < count)
        {
            token.ThrowIfCancellationRequested();
            double bestAffinity = 0;
            if (personalized)
                foreach (var candidate in pool)
                    if (!selected.Contains(candidate.Features.Track.Id)
                        && (candidate.Features.RecordingKey.Length == 0 || !recordings.Contains(candidate.Features.RecordingKey)))
                        bestAffinity = Math.Max(bestAffinity, candidate.Affinity);
            double relevanceFloor = Math.Max(0, bestAffinity - RelevanceTolerance);
            Candidate? best = null;
            for (int relax = 0; relax < 3 && best is null; relax++)
            {
                double score = double.NegativeInfinity;
                foreach (var candidate in pool)
                {
                    var f = candidate.Features;
                    if (selected.Contains(f.Track.Id) || (f.RecordingKey.Length > 0 && recordings.Contains(f.RecordingKey))) continue;
                    if (personalized && candidate.Affinity < relevanceFloor) continue;
                    int artistCount = MaxCount(artists, f.Artists);
                    int albumCount = f.AlbumKey.Length > 0 ? albums.GetValueOrDefault(f.AlbumKey) : 0;
                    if (relax < 2 && artistCount >= (relax == 0 ? 2 : 3)) continue;
                    if (relax == 0 && albumCount >= 1) continue;
                    // 只在相关性接近的候选中分散歌手/专辑；不累计惩罚用户喜欢的整个流派。
                    double genrePenalty = (personalized ? .008 : .06) * Math.Min(2, MaxCount(genres, f.Genres));
                    double adjusted = candidate.Score - genrePenalty - .045 * Math.Min(4, artistCount) - .08 * Math.Min(3, albumCount);
                    if (adjusted > score || (adjusted == score && (best is null || candidate.TieBreaker > best.Value.TieBreaker
                        || (candidate.TieBreaker == best.Value.TieBreaker && f.Track.Id < best.Value.Features.Track.Id))))
                    {
                        score = adjusted;
                        best = candidate;
                    }
                }
            }
            if (best is not { } chosen)
            {
                break;
            }
            var item = chosen.Features;
            selected.Add(item.Track.Id);
            if (item.RecordingKey.Length > 0) recordings.Add(item.RecordingKey);
            result.Add(new(item.Track.Id, chosen.Reason, chosen.Detail));
            Increment(artists, item.Artists);
            Increment(genres, item.Genres);
            if (item.AlbumKey.Length > 0) albums[item.AlbumKey] = albums.GetValueOrDefault(item.AlbumKey) + 1;
        }
        return result;
    }

    /// <summary>保守拆分明确的流派列表；未知值不形成一个虚假的流派组。</summary>
    public static string[] SplitGenres(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var result = new List<string>();
        var remaining = value.AsSpan();
        while (!remaining.IsEmpty)
        {
            int end = remaining.IndexOfAny(';', '|', ',');
            var part = (end < 0 ? remaining : remaining[..end]).Trim();
            if (IsKnown(part))
            {
                bool exists = false;
                foreach (string name in result) if (part.Equals(name.AsSpan(), StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                if (!exists) result.Add(part.ToString());
            }
            if (end < 0) break;
            remaining = remaining[(end + 1)..];
        }
        return result.ToArray();
    }

    private static bool IsKnown(ReadOnlySpan<char> value)
    {
        value = value.Trim();
        if (value.IsWhiteSpace()) return false;
        // 精确排除占位值，不能误伤 Unknown Pleasures 等真实专辑或艺术家名称。
        foreach (string unknown in UnknownNames)
            if (value.Equals(unknown.AsSpan(), StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static string[] FilterArtists(string[] values)
    {
        bool unchanged = true;
        for (int i = 0; i < values.Length && unchanged; i++)
        {
            string value = values[i];
            if (!IsKnown(value.AsSpan()) || value.AsSpan().Trim().Length != value.Length) { unchanged = false; break; }
            for (int j = 0; j < i; j++)
                if (value.Equals(values[j], StringComparison.OrdinalIgnoreCase)) { unchanged = false; break; }
        }
        if (unchanged) return values;
        var result = new List<string>(values.Length);
        foreach (string value in values)
        {
            var part = value.AsSpan().Trim();
            if (!IsKnown(part)) continue;
            bool duplicate = false;
            foreach (string existing in result)
                if (part.Equals(existing.AsSpan(), StringComparison.OrdinalIgnoreCase)) { duplicate = true; break; }
            if (!duplicate) result.Add(part.ToString());
        }
        return result.ToArray();
    }
    private static void Add(Dictionary<string, double> target, string[] names, double value)
    {
        foreach (string name in names) target[name] = target.GetValueOrDefault(name) + value / names.Length;
    }
    private static double ProfileMaximum(Dictionary<string, double> profile, Dictionary<string, int> support)
    {
        double max = 0;
        foreach (var pair in profile) max = Math.Max(max, pair.Value / Math.Sqrt(Math.Max(1, support.GetValueOrDefault(pair.Key))));
        return max;
    }
    private static double Match(Dictionary<string, double> profile, Dictionary<string, int> support, string[] values, double max)
    {
        if (max <= 0 || values.Length == 0) return 0;
        double score = 0;
        foreach (string value in values)
            score += Math.Sqrt(Math.Clamp(profile.GetValueOrDefault(value) / Math.Sqrt(Math.Max(1, support.GetValueOrDefault(value))) / max, 0, 1));
        return score / values.Length;
    }
    private static double ReadSignal<TKey>(IReadOnlyDictionary<TKey, float>? signals, TKey key) where TKey : notnull =>
        signals is not null && signals.TryGetValue(key, out float value) ? RecommendationEvidence.Signal(value) : 0;
    private static int MaxCount(Dictionary<string, int> counts, string[] values)
    {
        int max = 0;
        foreach (string value in values) max = Math.Max(max, counts.GetValueOrDefault(value));
        return max;
    }
    private static void Increment(Dictionary<string, int> counts, string[] values)
    {
        foreach (string value in values) counts[value] = counts.GetValueOrDefault(value) + 1;
    }
    private static double RandomScore(int id, uint seed)
    {
        uint x = unchecked((uint)id ^ seed);
        x ^= x >> 16;
        x = unchecked(x * 0x7feb352d);
        x ^= x >> 15;
        x = unchecked(x * 0x846ca68b);
        return (x ^ (x >> 16)) / (double)uint.MaxValue;
    }
    private static void Offer(PriorityQueue<Candidate, (double, double, int)> queue, Candidate value,
        int limit = CandidateLimit, bool weighCollection = false)
    {
        double weight = weighCollection ? value.Features.RecommendationWeight : 1;
        queue.Enqueue(value, (value.Score * weight, value.TieBreaker * weight, -value.Features.Track.Id));
        if (queue.Count > limit) queue.Dequeue();
    }
    private static List<Candidate> Drain(PriorityQueue<Candidate, (double, double, int)> queue)
    {
        var result = new List<Candidate>(queue.Count);
        while (queue.TryDequeue(out var item, out _)) result.Add(item);
        result.Reverse();
        return result;
    }
    private static List<Candidate> DrainSource(PriorityQueue<Candidate, (double, double, int)> queue,
        RecommendationRankFusion.SourceDistribution distribution)
    {
        var result = Drain(queue);
        for (int i = result.Count - 1; i >= 0; i--)
            if (!result[i].Features.IsCollected && !distribution.Retain(result[i].Score)) result.RemoveAt(i);
        return result;
    }
    private static void Merge(List<Candidate> target, List<Candidate> source)
    {
        var ids = new HashSet<int>();
        foreach (var item in target) ids.Add(item.Features.Track.Id);
        foreach (var item in source) if (ids.Add(item.Features.Track.Id)) target.Add(item);
    }
}
