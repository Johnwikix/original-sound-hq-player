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
                    IndexMemberTags(db, numbers, token);
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
                            if (db.Find<SongAutoLinkDirtyRecord>(song.Id) is not null ||
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

    // 一次只读取待处理组的标签。按成员建索引使混合组和拆分例外仍可手动关联。
    [System.Diagnostics.CodeAnalysis.DynamicDependency(System.Diagnostics.CodeAnalysis.DynamicallyAccessedMemberTypes.All, typeof(MemberTags))]
    private static void IndexMemberTags(SQLiteConnection db, string ids, CancellationToken token)
    {
        var members = db.Query<MemberTags>($"SELECT m.Id,m.SongId,m.Title,m.Author,m.Album,m.Path,m.TitleFromFileName,m.IsDetached,s.Title AS SongTitle,s.Artist AS SongArtist,s.Album AS SongAlbum FROM Music m JOIN Song s ON s.Id=m.SongId WHERE m.SongId IN ({ids}) ORDER BY m.SongId,m.Id");
        int start = 0;
        while (start < members.Count)
        {
            var first = members[start];
            MemberTags? representative = null;
            string? common = null;
            bool consistent = true;
            do
            {
                token.ThrowIfCancellationRequested();
                var member = members[start++];
                string? key = member.MatchKey();
                if (key is null)
                    db.Execute("DELETE FROM VersionMatch WHERE MusicId=?", member.Id);
                else
                    db.Execute("INSERT INTO VersionMatch VALUES(?,?) ON CONFLICT(MusicId) DO UPDATE SET MatchKey=excluded.MatchKey WHERE MatchKey<>excluded.MatchKey", member.Id, key);
                if (member.IsDetached) continue;
                if (representative is null)
                {
                    representative = member;
                    common = key;
                }
                else if (key != common) consistent = false;
            } while (start < members.Count && members[start].SongId == first.SongId);

            if (representative is null) continue;
            string? groupKey = consistent ? common : null;
            if (groupKey is not null && (representative.Title != first.SongTitle || representative.Author != first.SongArtist || representative.Album != first.SongAlbum) &&
                groupKey != SongVersionPolicy.GetMatchKey(first.SongTitle, first.SongArtist, first.SongAlbum))
                db.Execute("UPDATE Song SET Title=?,Artist=?,Album=?,Revision=Revision+1 WHERE Id=? AND (Title IS NOT ? OR Artist IS NOT ? OR Album IS NOT ?)",
                    representative.Title, representative.Author, representative.Album, first.SongId, representative.Title, representative.Author, representative.Album);
            db.Execute("INSERT INTO SongAutoLink(SongId,MatchKey,IsExcluded) VALUES(?,?,0) ON CONFLICT(SongId) DO UPDATE SET MatchKey=excluded.MatchKey",
                first.SongId, groupKey);
        }
    }

    private sealed class MemberTags
    {
        public int Id { get; set; }
        public int SongId { get; set; }
        public string Title { get; set; } = "";
        public string Author { get; set; } = "";
        public string Album { get; set; } = "";
        public string SongTitle { get; set; } = "";
        public string SongArtist { get; set; } = "";
        public string SongAlbum { get; set; } = "";
        public string Path { get; set; } = "";
        public bool? TitleFromFileName { get; set; }
        public bool IsDetached { get; set; }

        public string? MatchKey() => IsDetached ? null : SongVersionPolicy.FileMatchKey(Title, Author, Album, Path, TitleFromFileName);
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
