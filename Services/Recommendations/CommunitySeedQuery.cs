using SQLite;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.Recommendations;

public sealed record CommunitySeed(string Artist, string Title);

/// <summary>最多32位不同原始艺术家的正向候选；歌单成员只形成一次明确偏好。</summary>
public sealed class CommunitySeedCandidate
{
    public int Id { get; set; }
    public string Artist { get; set; } = "";
    public string Title { get; set; } = "";
    public bool IsPreferred { get; set; }
    public double ListeningWeight { get; set; }
}

public static class CommunitySeedQuery
{
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(CommunitySeedCandidate))]
    public static async Task<List<CommunitySeedCandidate>> ReadAsync(SQLiteAsyncConnection database, DateTime nowUtc,
        double qualifiedRatio, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var result = await database.QueryAsync<CommunitySeedCandidate>(
            "WITH listening AS (" + HomeListeningQuery.AggregateSql + "), " +
            "membership AS (SELECT DISTINCT pm.MusicId FROM PlayListMusic pm JOIN PlayList p ON p.Id=pm.PlayListId), " +
            "candidates AS (SELECT m.Id,m.Title,m.Author AS Artist, " +
            "(m.IsFavorite=1 OR p.MusicId IS NOT NULL) AS IsPreferred, COALESCE(h.Weight,0) AS ListeningWeight, " +
            "CASE WHEN m.IsFavorite=1 OR p.MusicId IS NOT NULL THEN 2.0 ELSE 0 END + " +
            "3.0*COALESCE(h.Weight,0)/(COALESCE(h.Weight,0)+3.0) AS Evidence " +
            "FROM Music m LEFT JOIN membership p ON p.MusicId=m.Id LEFT JOIN listening h ON h.MusicId=m.Id " +
            "WHERE (m.IsFavorite=1 OR p.MusicId IS NOT NULL OR h.Weight>0) AND TRIM(m.Author)<>'' AND TRIM(m.Title)<>''), " +
            "ranked AS (SELECT *,ROW_NUMBER() OVER(PARTITION BY TRIM(Artist) COLLATE NOCASE ORDER BY Evidence DESC,Id) AS ArtistRank FROM candidates) " +
            "SELECT Id,Artist,Title,IsPreferred,ListeningWeight FROM ranked WHERE ArtistRank=1 ORDER BY Evidence DESC,Id LIMIT 32",
            nowUtc.Ticks, nowUtc.AddDays(-90).Ticks, nowUtc.Ticks, qualifiedRatio).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>沿用曲库艺术家拆分规则，最多8个不同艺术家、每条原始曲目最多2位，保持请求上界16。</summary>
    public static List<CommunitySeed> Select(List<CommunitySeedCandidate> candidates, Func<string, string[]> splitArtists)
    {
        candidates.Sort(static (left, right) =>
        {
            int order = RecommendationEvidence.Strength(right.IsPreferred, right.ListeningWeight)
                .CompareTo(RecommendationEvidence.Strength(left.IsPreferred, left.ListeningWeight));
            return order != 0 ? order : left.Id.CompareTo(right.Id);
        });
        var result = new List<CommunitySeed>(8);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            int used = 0;
            foreach (string value in splitArtists(candidate.Artist))
            {
                string artist = value.Trim();
                if (artist.Length == 0 || !seen.Add(artist)) continue;
                result.Add(new(artist, candidate.Title));
                if (result.Count == 8) return result;
                if (++used == 2) break;
            }
        }
        return result;
    }
}
