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
    private static readonly string[] UnknownNames =
    [
        "未知", "未知艺术家", "未知专辑", "未知流派", "Unknown", "Unknown Artist", "Unknown Album", "Unknown Genre",
        "不明", "不明なアーティスト", "不明なアルバム", "不明なジャンル", "Неизвестный исполнитель", "Неизвестный артист",
        "Неизвестный альбом", "Неизвестный жанр", "Unbekannter Künstler", "Unbekanntes Album", "Unbekanntes Genre",
        "Artista desconocido", "Álbum desconocido", "Género desconocido", "Bilinmiyor", "Bilinmeyen Sanatçı", "Bilinmeyen Albüm", "Bilinmeyen Tür"
    ];
    private sealed record Features(RecommendationTrack Track, string[] Artists, string[] Genres, string AlbumKey);
    private readonly record struct Candidate(Features Features, double Score, RecommendationReason Reason, string Detail);

    /// <summary>从固定快照生成三个栏目；同一天、同一批次和输入产生相同结果。</summary>
    public HomeRecommendation Build(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history,
        DateTime nowUtc, int batch, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var listening = new Dictionary<int, HomeListening>(history.Count);
        foreach (var row in history) listening[row.MusicId] = row;
        var artists = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var years = new Dictionary<int, double>();
        var features = new List<Features>(tracks.Count);
        bool hasPreference = false;
        foreach (var track in tracks)
        {
            token.ThrowIfCancellationRequested();
            if (track.Id <= 0) continue;
            var artistNames = FilterArtists(track.Artists);
            var genreNames = SplitGenres(track.Genre);
            string albumKey = IsKnown(track.Album.AsSpan())
                ? string.Concat(IsKnown(track.AlbumArtist.AsSpan()) ? track.AlbumArtist.Trim() :
                    artistNames.Length > 0 ? artistNames[0] : "", "\u001f", track.Album.Trim()) : "";
            var item = new Features(track, artistNames, genreNames, albumKey);
            features.Add(item);
            listening.TryGetValue(track.Id, out var listened);
            double strength = Math.Log(1 + Math.Max(0, listened?.Weight ?? 0)) + (track.IsFavorite ? 2 : 0);
            if (strength <= 0) continue;
            hasPreference = true;
            Add(artists, artistNames, strength);
            Add(genres, genreNames, strength);
            if (albumKey.Length > 0) albums[albumKey] = albums.GetValueOrDefault(albumKey) + strength;
            if (track.Year is >= 1000 and <= 2999) years[track.Year / 10] = years.GetValueOrDefault(track.Year / 10) + strength;
        }

        double artistMax = Maximum(artists), genreMax = Maximum(genres), albumMax = Maximum(albums), yearMax = Maximum(years);
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
            double a = Match(artists, item.Artists, artistMax), g = Match(genres, item.Genres, genreMax);
            double album = albumMax > 0 && item.AlbumKey.Length > 0 ? albums.GetValueOrDefault(item.AlbumKey) / albumMax : 0;
            double year = yearMax > 0 && track.Year > 0 ? years.GetValueOrDefault(track.Year / 10) / yearMax : 0;
            double availableWeight = (artistMax > 0 && item.Artists.Length > 0 ? .45 : 0) +
                (genreMax > 0 && item.Genres.Length > 0 ? .30 : 0) +
                (albumMax > 0 && item.AlbumKey.Length > 0 ? .15 : 0) + (yearMax > 0 && track.Year > 0 ? .10 : 0);
            double content = availableWeight > 0 ? (.45 * a + .30 * g + .15 * album + .10 * year) / Math.Sqrt(availableWeight) : 0;
            double jitter = RandomScore(track.Id, seed);
            double repeat = Math.Min(1, Math.Log(1 + Math.Max(0, listened?.Weight ?? 0)) / 3);
            double novelty = 1 / (1 + (listened?.Count ?? 0) + Math.Log(1 + Math.Max(0, track.PlayCount)) * .15);
            double score = hasPreference ? .65 * content + .15 * repeat + (track.IsFavorite ? .12 : 0) + .08 * jitter : jitter;
            if (listened is not null && nowUtc.Ticks - listened.LastPlayedTicks < TimeSpan.TicksPerDay) score *= .75;
            var reason = track.IsFavorite ? RecommendationReason.Favorite : a > 0 && a * .45 >= g * .30
                ? RecommendationReason.Artist : g > 0 ? RecommendationReason.Genre : RecommendationReason.Explore;
            string detail = reason == RecommendationReason.Artist ? Best(artists, item.Artists) :
                reason == RecommendationReason.Genre ? Best(genres, item.Genres) : "";
            Offer(ranked, new(item, score, reason, detail));
            var discovery = new Candidate(item, .6 * novelty + .4 * jitter, RecommendationReason.Explore, "");
            Offer(explore, discovery);
            foreach (string genre in item.Genres)
                if (!genreRepresentatives.TryGetValue(genre, out var previous) || discovery.Score > previous.Score)
                    genreRepresentatives[genre] = discovery;
            if (listened is { Count: > 0 }) Offer(familiar, new(item, repeat + .05 * jitter, RecommendationReason.Familiar, ""));
        }

        var representatives = new PriorityQueue<Candidate, (double, int)>();
        foreach (var item in genreRepresentatives.Values) Offer(representatives, item);
        var pool = Drain(ranked);
        var exploration = Drain(explore);
        Merge(exploration, Drain(representatives));
        Merge(pool, exploration);
        var songs = Select(pool, exploration, PageSize, token);
        var seen = new HashSet<int>();
        foreach (var song in songs) seen.Add(song.MusicId);
        // 流派栏目优先补充主栏未出现且具有真实流派标签的歌曲。
        var discoveryPool = new List<Candidate>();
        if (genreRepresentatives.Count > 1)
            foreach (var item in exploration)
                if (item.Features.Genres.Length > 0 && !seen.Contains(item.Features.Track.Id))
                {
                    double unfamiliar = genreMax > 0 ? 1 - Match(genres, item.Features.Genres, genreMax) : 0;
                    discoveryPool.Add(item with { Score = item.Score + .4 * unfamiliar });
                }
        return new(songs, Select(Drain(familiar), null, PageSize, token),
            Select(discoveryPool, null, PageSize, token), !hasPreference);
    }

    private static List<RecommendedSong> Select(List<Candidate> pool, List<Candidate>? exploration, int count, CancellationToken token)
    {
        var result = new List<RecommendedSong>(count);
        var selected = new HashSet<int>();
        var artists = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var albums = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var genres = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (result.Count < count)
        {
            token.ThrowIfCancellationRequested();
            List<Candidate> candidates = exploration is not null && result.Count is 2 or 7 or 11 or 15 ? exploration : pool;
            Candidate? best = null;
            for (int relax = 0; relax < 3 && best is null; relax++)
            {
                double score = double.NegativeInfinity;
                foreach (var candidate in candidates)
                {
                    var f = candidate.Features;
                    if (selected.Contains(f.Track.Id)) continue;
                    int artistCount = MaxCount(artists, f.Artists);
                    int albumCount = f.AlbumKey.Length > 0 ? albums.GetValueOrDefault(f.AlbumKey) : 0;
                    if (relax < 2 && artistCount >= (relax == 0 ? 2 : 3)) continue;
                    if (relax == 0 && albumCount >= 1) continue;
                    double adjusted = candidate.Score - .28 * MaxCount(genres, f.Genres) - .18 * artistCount - .24 * albumCount;
                    if (adjusted > score || (adjusted == score && f.Track.Id < best?.Features.Track.Id))
                    {
                        score = adjusted;
                        best = candidate;
                    }
                }
            }
            if (best is not { } chosen)
            {
                if (candidates != pool) { exploration = null; continue; }
                break;
            }
            var item = chosen.Features;
            selected.Add(item.Track.Id);
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
    private static double Match(Dictionary<string, double> profile, string[] values, double max)
    {
        if (max <= 0 || values.Length == 0) return 0;
        double score = 0;
        foreach (string value in values) score += profile.GetValueOrDefault(value) / max;
        return score / values.Length;
    }
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
