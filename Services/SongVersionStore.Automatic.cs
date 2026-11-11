using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

public sealed partial class SongVersionStore
{
    private readonly SemaphoreSlim _automaticGate = new(1, 1);

    /// <summary>处理旧库和增量变更，按完整三项标签自动关联；取消或失败后的待办保存在数据库中。</summary>
    public async Task<int> ApplyAutomaticLinksAsync(LyricsRepository lyrics, CancellationToken token = default)
    {
        await _automaticGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            int merged = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                bool indexed = false;
                await database.RunInTransactionAsync(db =>
                {
                    token.ThrowIfCancellationRequested();
                    var dirty = db.Query<SongAutoLinkDirtyRecord>("SELECT * FROM SongAutoLinkDirty ORDER BY SongId LIMIT 512");
                    if (dirty.Count == 0) return;
                    indexed = true;
                    List<int> ids = new(dirty.Count);
                    foreach (var row in dirty) ids.Add(row.SongId);
                    string numbers = string.Join(',', ids);
                    var songs = db.Query<SongRecord>($"SELECT * FROM Song WHERE Id IN ({numbers}) AND MergedIntoSongId IS NULL " +
                        "AND EXISTS(SELECT 1 FROM Music WHERE SongId=Song.Id AND IsDetached=0)");
                    foreach (var song in songs)
                    {
                        token.ThrowIfCancellationRequested();
                        string? key = SongVersionPolicy.GetMatchKey(song.Title, song.Artist, song.Album);
                        // UPSERT 保留用户拆分例外；主键记录也是持久化的退出恢复游标。
                        db.Execute("INSERT INTO SongAutoLink(SongId,MatchKey,IsExcluded) VALUES(?,?,0) " +
                            "ON CONFLICT(SongId) DO UPDATE SET MatchKey=excluded.MatchKey", song.Id, key);
                    }
                    db.Execute($"DELETE FROM SongAutoLink WHERE SongId IN ({numbers}) AND NOT EXISTS(SELECT 1 FROM Song " +
                        "WHERE Id=SongAutoLink.SongId AND MergedIntoSongId IS NULL AND EXISTS(SELECT 1 FROM Music WHERE SongId=Song.Id AND IsDetached=0))");
                    db.Execute($"INSERT OR IGNORE INTO SongAutoLinkPending(MatchKey) SELECT DISTINCT a.MatchKey FROM SongAutoLink a " +
                        $"WHERE a.SongId IN ({numbers}) AND a.IsExcluded=0 AND a.MatchKey IS NOT NULL AND " +
                        "EXISTS(SELECT 1 FROM SongAutoLink b JOIN Song s ON s.Id=b.SongId WHERE b.MatchKey=a.MatchKey AND b.SongId<>a.SongId AND b.IsExcluded=0 " +
                        "AND s.MergedIntoSongId IS NULL AND EXISTS(SELECT 1 FROM Music WHERE SongId=s.Id AND IsDetached=0))");
                    db.Execute($"DELETE FROM SongAutoLinkDirty WHERE SongId IN ({numbers})");
                }).ConfigureAwait(false);
                // 建索引按有界批次提交；全部完成后再合并，避免一个大组被反复合并、重复快照。
                if (indexed) continue;
                var pending = await database.QueryAsync<SongAutoLinkPendingRecord>("SELECT * FROM SongAutoLinkPending ORDER BY MatchKey LIMIT 64").ConfigureAwait(false);
                if (pending.Count == 0) return merged;
                object[] keys = new object[pending.Count];
                for (int i = 0; i < pending.Count; i++) keys[i] = pending[i].MatchKey;
                string parameters = string.Join(',', System.Linq.Enumerable.Repeat("?", keys.Length));
                var candidates = await database.QueryAsync<SongRecord>($"SELECT s.* FROM Song s JOIN SongAutoLink a ON a.SongId=s.Id " +
                    $"WHERE a.MatchKey IN ({parameters}) AND a.IsExcluded=0 AND s.MergedIntoSongId IS NULL " +
                    "AND EXISTS(SELECT 1 FROM Music WHERE SongId=s.Id AND IsDetached=0) ORDER BY s.Id", keys).ConfigureAwait(false);
                List<int> songIds = new(candidates.Count);
                foreach (var song in candidates) songIds.Add(song.Id);
                await lyrics.EnsureSharedForSongsAsync(songIds, token).ConfigureAwait(false);
                foreach (var item in pending)
                {
                    token.ThrowIfCancellationRequested();
                    await database.RunInTransactionAsync(db =>
                    {
                        token.ThrowIfCancellationRequested();
                        var songs = db.Query<SongRecord>("SELECT s.* FROM Song s JOIN SongAutoLink a ON a.SongId=s.Id " +
                            "WHERE a.MatchKey=? AND a.IsExcluded=0 AND s.MergedIntoSongId IS NULL " +
                            "AND EXISTS(SELECT 1 FROM Music WHERE SongId=s.Id AND IsDetached=0) ORDER BY s.Id", item.MatchKey);
                        if (songs.Count < 2)
                        {
                            db.Delete<SongAutoLinkPendingRecord>(item.MatchKey);
                            return;
                        }
                        // 读取歌词期间可能有新文件或标签提交；保留待办，下一轮补齐文档并重新检查。
                        foreach (var song in songs)
                            if (SongVersionPolicy.GetMatchKey(song.Title, song.Artist, song.Album) != item.MatchKey ||
                                db.Find<SongLyricsRecord>(song.Id) is null) return;
                        MergeAutomaticBucket(db, songs, token);
                        merged += songs.Count - 1;
                        db.Delete<SongAutoLinkPendingRecord>(item.MatchKey);
                    }).ConfigureAwait(false);
                }
            }
        }
        finally { _automaticGate.Release(); }
    }

    private static void MergeAutomaticBucket(SQLiteConnection db, List<SongRecord> songs, CancellationToken token)
    {
        int[] ids = new int[songs.Count];
        for (int i = 0; i < songs.Count; i++) ids[i] = songs[i].Id;
        var before = Capture(db, ids);
        var target = songs[0]; // 最早的歌曲身份保持稳定，已有首选优先保留。
        if (target.PreferredMusicId is int pinned && db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Music WHERE Id=? AND SongId=? AND IsDetached=0", pinned, target.Id) != 1)
            target.PreferredMusicId = null;
        var chosen = before.Lyrics.Find(row => row.SongId == target.Id)!;
        if (!Meaningful(chosen))
        {
            chosen = before.Lyrics.Find(row => row.SourceKind == "User") ??
                before.Lyrics.Find(Meaningful) ?? chosen;
        }
        var shared = SongLyricsRecord.From(target.Id, chosen.Snapshot());
        long revision = 0;
        foreach (var doc in before.Lyrics)
        {
            token.ThrowIfCancellationRequested();
            revision = Math.Max(revision, doc.Revision);
            if (doc.Snapshot().Document == chosen.Snapshot().Document || !Meaningful(doc)) continue;
            // 每个来源一条 INSERT SELECT；已有覆盖和显式清空都保留，空白缺省继承共享歌词。
            db.Execute("INSERT OR IGNORE INTO VersionLyricsOverride(MusicId,Lyrics,LyricsFormat,TranslatedLyrics,PronunciationLyrics,Revision,SourceKind,SourceKey,Diagnostic) " +
                "SELECT Id,?,?,?,?,?,?,?,? FROM Music WHERE SongId=?", doc.Lyrics, (int)doc.LyricsFormat, doc.TranslatedLyrics,
                doc.PronunciationLyrics, doc.Revision + 1, doc.SourceKind, doc.SourceKey, doc.Diagnostic, doc.SongId);
        }
        shared.Revision = revision + 1;
        db.InsertOrReplace(shared);
        foreach (var song in songs)
        {
            target.IsFavorite |= song.IsFavorite;
            target.FavoriteOrder = Math.Max(target.FavoriteOrder, song.FavoriteOrder);
            if (target.PreferredMusicId is null && song.PreferredMusicId is int preferred &&
                db.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE Id=? AND SongId=? AND IsDetached=0", preferred, song.Id) == 1)
                target.PreferredMusicId = preferred;
        }
        target.Revision++;
        db.Update(target);
        string numbers = string.Join(',', ids);
        db.Execute($"UPDATE Song SET MergedIntoSongId=?,Revision=Revision+1 WHERE Id IN ({numbers}) AND Id<>?", target.Id, target.Id);
        db.Execute($"UPDATE Music SET SongId=?,IsFavorite=?,[Order]=? WHERE SongId IN ({numbers})", target.Id, target.IsFavorite, target.FavoriteOrder);
        db.Execute($"UPDATE PlayListMusic SET SongId=? WHERE SongId IN ({numbers})", target.Id);
        db.Execute($"DELETE FROM SongAutoLink WHERE SongId IN ({numbers}) AND SongId<>?", target.Id);
        SaveOperation(db, target.Id, "AutoMerge", before, Capture(db, ids));
    }

    private static bool Meaningful(SongLyricsRecord doc) => doc.SourceKind == "User" ||
        !string.IsNullOrWhiteSpace(doc.Lyrics) || !string.IsNullOrWhiteSpace(doc.TranslatedLyrics) || !string.IsNullOrWhiteSpace(doc.PronunciationLyrics);

    // v1 已有手动拆分/撤销记录时，升级为自动关联也必须尊重这些决定。
    private static void RestoreAutomaticExclusions(SQLiteConnection db)
    {
        foreach (var operation in db.Query<SongVersionOperationRecord>("SELECT * FROM SongVersionOperation ORDER BY Id"))
        {
            if (operation.Kind == "Split" && !operation.Undone)
            {
                var before = JsonSerializer.Deserialize(operation.BeforeJson, SongVersionJsonContext.Default.SongVersionSnapshot) ?? throw Conflict();
                var after = JsonSerializer.Deserialize(operation.AfterJson, SongVersionJsonContext.Default.SongVersionSnapshot) ?? throw Conflict();
                foreach (var song in after.Songs)
                    if (!before.Songs.Exists(row => row.Id == song.Id))
                        db.InsertOrReplace(new SongAutoLinkRecord { SongId = song.Id, IsExcluded = true });
            }
            else if (operation.Kind is "Merge" or "AutoMerge")
            {
                if (operation.Undone)
                {
                    var before = JsonSerializer.Deserialize(operation.BeforeJson, SongVersionJsonContext.Default.SongVersionSnapshot) ?? throw Conflict();
                    foreach (var song in before.Songs)
                        db.InsertOrReplace(new SongAutoLinkRecord { SongId = song.Id, IsExcluded = true });
                }
                else db.Execute("UPDATE SongAutoLink SET IsExcluded=0 WHERE SongId=?", operation.SongId);
            }
        }
    }
}
