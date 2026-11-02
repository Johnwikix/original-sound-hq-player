using System;
using System.Collections.Generic;
using System.Threading;
using WinUIMusicPlayer.Model.Recommendations;

namespace WinUIMusicPlayer.Services.Recommendations;

/// <summary>仅使用本地快照生成推荐，以有限候选重排控制艺术家、专辑和流派多样性。</summary>
public sealed class HomeRecommendationService
{
    private const int CandidateLimit = 200;
    private const int PageSize = 20;
    private const double MetadataWeight = .25;
    private const double AudioWeight = .50;
    private const double CommunityWeight = .25;
    private const double StrongestSourceWeight = .60;
    private const double RelevanceTolerance = .12;
    private const double MinimumDiscoveryAffinity = .25;
    private static readonly string[] UnknownNames =
    [
        "未知", "未知艺术家", "未知专辑", "未知流派", "Unknown", "Unknown Artist", "Unknown Album", "Unknown Genre",
        "不明", "不明なアーティスト", "不明なアルバム", "不明なジャンル", "Неизвестный исполнитель", "Неизвестный артист",
        "Неизвестный альбом", "Неизвестный жанр", "Unbekannter Künstler", "Unbekanntes Album", "Unbekanntes Genre",
        "Artista desconocido", "Álbum desconocido", "Género desconocido", "Bilinmiyor", "Bilinmeyen Sanatçı", "Bilinmeyen Albüm", "Bilinmeyen Tür"
    ];
    private readonly record struct Features(RecommendationTrack Track, string[] Artists, string[] Genres, string AlbumKey, string RecordingKey);
    private readonly record struct Candidate(Features Features, double Score, double Affinity, RecommendationReason Reason, string Detail);

    /// <summary>从固定快照生成三个栏目；同一天、同一批次和输入产生相同结果。</summary>
    public HomeRecommendation Build(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history,
        DateTime nowUtc, int batch, CancellationToken token,
        IReadOnlyDictionary<int, float>? audio = null, CommunitySignals? community = null)
    {
        token.ThrowIfCancellationRequested();
        var listening = new Dictionary<int, HomeListening>(history.Count);
        foreach (var row in history) listening[row.MusicId] = row;
        var artists = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var years = new Dictionary<int, double>();
        var artistSupport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genreSupport = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var features = new List<Features>(tracks.Count);
        var genreCache = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var albumKeys = new Dictionary<(string Artist, string Album), string>();
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
            Increment(artistSupport, artistNames);
            Increment(genreSupport, genreNames);
            listening.TryGetValue(track.Id, out var listened);
            double strength = RecommendationEvidence.Strength(track, listened);
            if (strength <= 0) continue;
            hasPreference = true;
            Add(artists, artistNames, strength);
            Add(genres, genreNames, strength);
            if (albumKey.Length > 0) albums[albumKey] = albums.GetValueOrDefault(albumKey) + strength;
            if (track.Year is >= 1000 and <= 2999) years[track.Year / 10] = years.GetValueOrDefault(track.Year / 10) + strength;
        }

        double artistMax = ProfileMaximum(artists, artistSupport), genreMax = ProfileMaximum(genres, genreSupport);
        double albumMax = Maximum(albums), yearMax = Maximum(years);
        bool hasAudio = false;
        bool hasCommunity = false;
        bool audioAvailable = audio is { Count: > 0 };
        bool communityAvailable = community is { Artists.Count: > 0 } or { Tracks.Count: > 0 };
        if (audioAvailable || communityAvailable)
        {
            foreach (var item in features)
            {
                if (!item.Track.IsAvailable) continue;
                if (audioAvailable && !hasAudio && ReadSignal(audio, item.Track.Id) > 0) hasAudio = true;
                if (communityAvailable && !hasCommunity && community is not null)
                    foreach (string artist in item.Artists)
                        if (ReadSignal(community.Artists, artist) > 0 || (community.Tracks.Count > 0
                            && ReadSignal(community.Tracks, CommunitySignals.TrackKey(artist, item.Track.Title)) > 0))
                        {
                            hasCommunity = true;
                            break;
                        }
                if ((!audioAvailable || hasAudio) && (!communityAvailable || hasCommunity)) break;
            }
        }
        hasPreference |= hasAudio || hasCommunity;
        double evidenceWeight = (artistMax > 0 || genreMax > 0 || albumMax > 0 || yearMax > 0 ? MetadataWeight : 0)
            + (hasAudio ? AudioWeight : 0) + (hasCommunity ? CommunityWeight : 0);
        var ranked = new PriorityQueue<Candidate, (double, int)>();
        var explore = new PriorityQueue<Candidate, (double, int)>();
        var familiar = new PriorityQueue<Candidate, (double, int)>();
        var genreRepresentatives = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
        uint seed = unchecked((uint)(nowUtc.Date.Ticks / TimeSpan.TicksPerDay) * 16777619u ^ (uint)batch * 2246822519u);
        foreach (var item in features)
        {
            token.ThrowIfCancellationRequested();
            var track = item.Track;
            if (!track.IsAvailable) continue;
            listening.TryGetValue(track.Id, out var listened);
            double a = Match(artists, artistSupport, item.Artists, artistMax);
            double g = Match(genres, genreSupport, item.Genres, genreMax);
            double album = albumMax > 0 && item.AlbumKey.Length > 0 ? albums.GetValueOrDefault(item.AlbumKey) / albumMax : 0;
            double year = yearMax > 0 && track.Year > 0 ? years.GetValueOrDefault(track.Year / 10) / yearMax : 0;
            double availableWeight = (artistMax > 0 && item.Artists.Length > 0 ? .42 : 0) +
                (genreMax > 0 && item.Genres.Length > 0 ? .43 : 0) +
                (albumMax > 0 && item.AlbumKey.Length > 0 ? .12 : 0) + (yearMax > 0 && track.Year > 0 ? .03 : 0);
            // 年代/专辑只是辅助证据，缺标签时不能把仅同年发行放大为完整风格匹配。
            double content = (.42 * a + .43 * g + .12 * album + .03 * year) / Math.Max(.65, availableWeight);
            double jitter = RandomScore(track.Id, seed);
            double repeat = RecommendationEvidence.Strength(false, listened) / 3;
            double novelty = 1 / (1 + Math.Max(0, listened?.Count ?? 0) + Math.Log(1 + Math.Max(0, track.PlayCount)) * .15);
            double acoustic = ReadSignal(audio, track.Id);
            double remote = 0;
            if (hasCommunity && community is not null)
                foreach (string artist in item.Artists)
                {
                    remote = Math.Max(remote, ReadSignal(community.Artists, artist));
                    if (track.Title.Length > 0) remote = Math.Max(remote, ReadSignal(community.Tracks, CommunitySignals.TrackKey(artist, track.Title)));
                }
            // 强音频/社区匹配可以独立召回陌生歌手；交叉印证再加权，缺失来源不填造负反馈。
            double strongest = Math.Max(content, Math.Max(acoustic, remote));
            double blended = evidenceWeight > 0 ? (MetadataWeight * content + AudioWeight * acoustic + CommunityWeight * remote) / evidenceWeight : 0;
            double affinity = StrongestSourceWeight * strongest + (1 - StrongestSourceWeight) * blended;
            double score = hasPreference ? .86 * affinity + (track.IsFavorite || track.IsInPlaylist ? .06 : 0) + .05 * novelty + .03 * jitter : jitter;
            if (listened is { LastPlayedTicks: > 0 })
            {
                double age = Math.Max(0, (nowUtc.Ticks - listened.LastPlayedTicks) / (double)TimeSpan.TicksPerDay);
                score -= .05 * Math.Max(0, 1 - age);
            }
            var reason = track.IsFavorite ? RecommendationReason.Favorite : a > 0 && a >= g
                ? RecommendationReason.Artist : g > 0 ? RecommendationReason.Genre : RecommendationReason.Explore;
            string detail = reason == RecommendationReason.Artist ? Best(artists, item.Artists) :
                reason == RecommendationReason.Genre ? Best(genres, item.Genres) : "";
            if (remote >= .4 && remote >= content && remote > acoustic) { reason = RecommendationReason.Community; detail = ""; }
            else if (acoustic >= .4 && acoustic >= content) { reason = RecommendationReason.Audio; detail = ""; }
            Offer(ranked, new(item, score, affinity, reason, detail));
            double discoveryScore = hasPreference ? .80 * affinity + .15 * novelty + .05 * jitter : .6 * novelty + .4 * jitter;
            var discovery = new Candidate(item, discoveryScore, affinity, reason, detail);
            // 在截断候选之前排除已听曲目，防止大量高分老歌占满探索候选池。
            if (track.PlayCount == 0 && listened is not { Count: > 0 } && (!hasPreference || affinity >= MinimumDiscoveryAffinity))
            {
                Offer(explore, discovery);
                foreach (string genre in item.Genres)
                    if (!genreRepresentatives.TryGetValue(genre, out var previous) || discovery.Score > previous.Score)
                        genreRepresentatives[genre] = discovery;
            }
            if (listened is { Count: > 0 }) Offer(familiar, new(item, .75 * repeat + .20 * affinity + .05 * jitter, affinity, RecommendationReason.Familiar, ""));
        }

        var representatives = new PriorityQueue<Candidate, (double, int)>();
        foreach (var item in genreRepresentatives.Values) Offer(representatives, item);
        var pool = Drain(ranked);
        var exploration = Drain(explore);
        Merge(exploration, Drain(representatives));
        Merge(pool, exploration);
        var seen = new HashSet<int>();
        var recordings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 全页共享排除集合：常听、精选、主栏、探索分别分配独立曲目。
        var familiarSongs = Select(Drain(familiar), Math.Min(PageSize, features.Count / 4), token, seen, recordings, hasPreference);
        int mainReserve = Math.Min(PageSize, Math.Max(1, features.Count - familiarSongs.Count));
        int featuredCount = Math.Min(5, Math.Max(0, features.Count - familiarSongs.Count - mainReserve));
        var featured = Select(pool, featuredCount, token, seen, recordings, hasPreference);
        int cardCount = Math.Min(3, Math.Max(0, features.Count - familiarSongs.Count - featured.Count - mainReserve));
        var cards = Select(pool, cardCount, token, seen, recordings, hasPreference);
        var songs = Select(pool, PageSize, token, seen, recordings, hasPreference);
        // 同曲版本很多时，源文件数不能代表独立录音数，至少保留一个主栏入口。
        if (songs.Count == 0)
        {
            var donor = featured.Count > 0 ? featured : familiarSongs;
            if (donor.Count > 0) { songs.Add(donor[0]); donor.RemoveAt(0); }
        }
        var discoveryPool = new List<Candidate>();
        if (hasPreference || genreRepresentatives.Count > 1)
            foreach (var item in exploration)
                if (!seen.Contains(item.Features.Track.Id) && (!hasPreference || item.Affinity >= MinimumDiscoveryAffinity)
                    && item.Features.Track.PlayCount == 0 && listening.GetValueOrDefault(item.Features.Track.Id) is not { Count: > 0 })
                {
                    discoveryPool.Add(item);
                }
        return new(songs, familiarSongs, Select(discoveryPool, PageSize, token, seen, recordings, hasPreference), !hasPreference, featured, cards);
    }

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
                    if (adjusted > score || (adjusted == score && f.Track.Id < best?.Features.Track.Id))
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
        foreach (string value in values)
            if (!IsKnown(value.AsSpan()) || value.AsSpan().Trim().Length != value.Length) { unchanged = false; break; }
        if (unchanged) return values;
        var result = new List<string>(values.Length);
        foreach (string value in values)
            if (IsKnown(value.AsSpan())) result.Add(value.Trim());
        return result.ToArray();
    }
    private static void Add(Dictionary<string, double> target, string[] names, double value)
    {
        foreach (string name in names) target[name] = target.GetValueOrDefault(name) + value / names.Length;
    }
    private static double Maximum<T>(Dictionary<T, double> values) where T : notnull
    {
        double max = 0;
        foreach (double value in values.Values) max = Math.Max(value, max);
        return max;
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
    private static string Best(Dictionary<string, double> profile, string[] values)
    {
        string result = "";
        double best = 0;
        foreach (string value in values)
            if (profile.GetValueOrDefault(value) > best) { best = profile[value]; result = value; }
        return result;
    }
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
    private static void Offer(PriorityQueue<Candidate, (double, int)> queue, Candidate value)
    {
        queue.Enqueue(value, (value.Score, -value.Features.Track.Id));
        if (queue.Count > CandidateLimit) queue.Dequeue();
    }
    private static List<Candidate> Drain(PriorityQueue<Candidate, (double, int)> queue)
    {
        var result = new List<Candidate>(queue.Count);
        while (queue.TryDequeue(out var item, out _)) result.Add(item);
        result.Reverse();
        return result;
    }
    private static void Merge(List<Candidate> target, List<Candidate> source)
    {
        var ids = new HashSet<int>();
        foreach (var item in target) ids.Add(item.Features.Track.Id);
        foreach (var item in source) if (ids.Add(item.Features.Track.Id)) target.Add(item);
    }
}
