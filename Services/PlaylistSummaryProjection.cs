using System.Collections.Generic;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

/// <summary>在 UI 线程按同一份库/成员快照更新歌单卡片；封面与详情默认排序一致。</summary>
internal static class PlaylistSummaryProjection
{
    public static void Refresh(IReadOnlyList<PlayList> playlists, IReadOnlyList<PlayListMusic> mappings, LibraryQueries queries)
    {
        if (playlists.Count == 0) return;
        var summaries = new Dictionary<int, (int Count, int Order, Music? Cover)>(playlists.Count);
        for (int i = 0; i < playlists.Count; i++)
            summaries.TryAdd(playlists[i].Id, (0, int.MinValue, null));

        // 一次遍历所有成员，避免每张卡片扫描、排序、分配整份歌单。
        for (int i = 0; i < mappings.Count; i++)
        {
            var mapping = mappings[i];
            if (!summaries.TryGetValue(mapping.PlayListId, out var summary)
                || !queries.TryFindById(mapping.MusicId, out var music) || music is null) continue;
            summary.Count++;
            if (summary.Cover is null || mapping.Order > summary.Order)
            {
                summary.Order = mapping.Order;
                summary.Cover = music;
            }
            summaries[mapping.PlayListId] = summary;
        }

        for (int i = 0; i < playlists.Count; i++)
        {
            var playlist = playlists[i];
            var summary = summaries[playlist.Id];
            playlist.SongCount = summary.Count;
            playlist.CoverMusic = summary.Cover;
        }
    }
}
