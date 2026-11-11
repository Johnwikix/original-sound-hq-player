using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    // 在批次已提交、嵌入歌词已入库后处理；只投影本批次，不在每次合并后刷新整个 UI。
    private async Task<int> ApplyAutomaticSongLinksAsync(IReadOnlyList<Music> batch, CancellationToken token = default)
    {
        if (Versions is null) return 0;
        int merged = await Versions.ApplyAutomaticLinksAsync(Lyrics, token);
        if (batch.Count == 0) return merged;
        List<int> ids = new(batch.Count);
        foreach (var music in batch) if (music.Id > 0) ids.Add(music.Id);
        if (ids.Count == 0) return merged;
        var current = await _dbConnection.QueryAsync<Music>($"SELECT * FROM Music WHERE Id IN ({string.Join(',', ids)})");
        await Versions.DecorateAsync(current);
        Dictionary<int, Music> byId = new(current.Count);
        foreach (var music in current) byId[music.Id] = music;
        foreach (var music in batch)
        {
            if (!byId.TryGetValue(music.Id, out var row)) continue;
            music.SongId = row.SongId;
            music.IsFavorite = row.IsFavorite;
            music.Order = row.Order;
            music.SongPreferredMusicId = row.SongPreferredMusicId;
            music.HasVersionLyrics = row.HasVersionLyrics;
        }
        return merged;
    }
}
