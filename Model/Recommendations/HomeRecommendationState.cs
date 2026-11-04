using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace WinUIMusicPlayer.Model.Recommendations;

/// <summary>保存下一批次和最近曝光；只持久化有界ID快照，不保存Music或推荐评分。</summary>
public sealed class HomeRecommendationState
{
    internal const int RecentBatchLimit = 3;
    internal const int SongsPerBatchLimit = 68;
    internal const long ExposureLifetimeTicks = 7 * TimeSpan.TicksPerDay;

    public int SchemaVersion { get; init; } = 1;
    public int NextBatch { get; init; }
    public HomeRecommendationExposure[] Recent { get; init; } = [];

    public static HomeRecommendationState CreateNew() => new() { NextBatch = Random.Shared.Next(int.MaxValue) };

    internal static bool IsRecentExposure(long ticks, DateTime nowUtc) =>
        ticks > 0 && ticks <= nowUtc.Ticks && nowUtc.Ticks - ticks < ExposureLifetimeTicks;

    /// <summary>过期、未来时间和空记录不占据最近三批名额，保留首次曝光时间而非读取时间。</summary>
    public HomeRecommendationState Normalize(DateTime nowUtc)
    {
        if (SchemaVersion != 1) throw new NotSupportedException("Unsupported home recommendation state schema.");
        var result = new List<HomeRecommendationExposure>(RecentBatchLimit);
        foreach (var exposure in Recent ?? [])
        {
            if (result.Count == RecentBatchLimit) break;
            if (exposure is null || !IsRecentExposure(exposure.ExposedAtUtcTicks, nowUtc)) continue;
            var ids = new List<int>(SongsPerBatchLimit);
            var seen = new HashSet<int>();
            foreach (int id in exposure.MusicIds ?? [])
            {
                if (id > 0 && seen.Add(id)) ids.Add(id);
                if (ids.Count == SongsPerBatchLimit) break;
            }
            if (ids.Count > 0)
                result.Add(new() { ExposedAtUtcTicks = exposure.ExposedAtUtcTicks, MusicIds = ids.ToArray() });
        }
        return new() { NextBatch = NextBatch, Recent = result.ToArray() };
    }

    public static HomeRecommendationState Capture(int publishedBatch, IReadOnlyList<HomeRecommendation> recent, DateTime nowUtc)
    {
        var exposures = new List<HomeRecommendationExposure>(RecentBatchLimit);
        foreach (var page in recent)
        {
            if (exposures.Count == RecentBatchLimit) break;
            if (!IsRecentExposure(page.ExposedAtUtcTicks, nowUtc)) continue;
            var ids = new List<int>(SongsPerBatchLimit);
            var seen = new HashSet<int>();
            Add(page.Songs);
            Add(page.Familiar);
            Add(page.Discovery);
            Add(page.Featured);
            Add(page.Cards);
            if (ids.Count > 0)
                exposures.Add(new() { ExposedAtUtcTicks = page.ExposedAtUtcTicks, MusicIds = ids.ToArray() });

            void Add(IReadOnlyList<RecommendedSong>? songs)
            {
                if (songs is null) return;
                foreach (var song in songs)
                {
                    if (ids.Count == SongsPerBatchLimit) break;
                    if (song.MusicId > 0 && seen.Add(song.MusicId)) ids.Add(song.MusicId);
                }
            }
        }
        return new() { NextBatch = unchecked(publishedBatch + 1), Recent = exposures.ToArray() };
    }

    /// <summary>恢复结果只作为曝光输入，不作为首页显示或播放队列。</summary>
    public HomeRecommendation[] Restore(DateTime nowUtc)
    {
        var state = Normalize(nowUtc);
        var pages = new HomeRecommendation[state.Recent.Length];
        for (int i = 0; i < pages.Length; i++)
        {
            var exposure = state.Recent[i];
            var songs = new RecommendedSong[exposure.MusicIds.Length];
            for (int j = 0; j < songs.Length; j++) songs[j] = new(exposure.MusicIds[j], RecommendationReason.Explore);
            pages[i] = new(songs, [], [], false) { ExposedAtUtcTicks = exposure.ExposedAtUtcTicks };
        }
        return pages;
    }
}

public sealed class HomeRecommendationExposure
{
    public long ExposedAtUtcTicks { get; init; }
    public int[] MusicIds { get; init; } = [];
}

[JsonSerializable(typeof(HomeRecommendationState))]
internal partial class HomeRecommendationStateJsonContext : JsonSerializerContext { }
