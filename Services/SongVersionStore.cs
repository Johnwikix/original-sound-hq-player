using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.Lyrics;

namespace WinUIMusicPlayer.Services;

/// <summary>持久化歌曲归属、首选及可恢复操作；不拥有连接或 UI 状态。</summary>
public sealed partial class SongVersionStore(SQLiteAsyncConnection database)
{
    /// <summary>备份旧库并幂等补齐一对一歌曲，初始化完成前不公开新结构。</summary>
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongLyricsRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(VersionLyricsOverrideRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongVersionOperationRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongVersionSchemaRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongAutoLinkRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongAutoLinkDirtyRecord))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(SongAutoLinkPendingRecord))]
    public async Task InitializeAsync(string path, Action<SQLiteConnection>? migrateSourceScopes = null)
    {
        bool exists = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Song'") > 0;
        int previous = exists && await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='SongVersionSchema'") > 0
            ? await database.ExecuteScalarAsync<int>("SELECT Version FROM SongVersionSchema WHERE Id=1") : 0;
        string? backup = previous switch
        {
            1 => path + ".autolink-v2.bak",
            2 => path + ".song-v3.bak",
            _ => null
        };
        if (!exists && !path.EndsWith(".song-v1.db", StringComparison.OrdinalIgnoreCase) && await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Music") > 0)
            backup = path + ".song-v1.bak";
        if (backup is not null && !File.Exists(backup))
        {
            string temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await database.BackupAsync(temporary);
                File.Move(temporary, backup, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        await database.CreateTableAsync<SongRecord>();
        await database.CreateTableAsync<SongLyricsRecord>();
        await database.CreateTableAsync<VersionLyricsOverrideRecord>();
        await database.CreateTableAsync<SongVersionOperationRecord>();
        await database.CreateTableAsync<SongVersionSchemaRecord>();
        var schema = await database.FindAsync<SongVersionSchemaRecord>(1);
        if (schema is { Version: > 3 }) throw new InvalidOperationException("Unsupported song version schema.");
        await database.CreateTableAsync<SongAutoLinkRecord>();
        await database.CreateTableAsync<SongAutoLinkDirtyRecord>();
        await database.CreateTableAsync<SongAutoLinkPendingRecord>();
        await database.RunInTransactionAsync(db =>
        {
            if (schema is null)
                db.Execute("UPDATE Music SET SongId=COALESCE(SongId,0),MetadataRevision=COALESCE(MetadataRevision,0),CodecId=COALESCE(CodecId,'')," +
                    "EncodingKind=COALESCE(EncodingKind,0),IsMissing=COALESCE(IsMissing,0),IsDetached=COALESCE(IsDetached,0)");
            db.Execute("CREATE INDEX IF NOT EXISTS IX_Song_LyricsSource ON Song(LyricsSourceMusicId)");
            db.Execute("INSERT INTO Song(Title,Artist,Album,IsFavorite,FavoriteOrder,Revision,LyricsSourceMusicId) " +
                "SELECT Title,Author,Album,IsFavorite,[Order],1,Id FROM Music WHERE SongId=0 OR SongId IS NULL ORDER BY Id");
            db.Execute("UPDATE Music SET SongId=(SELECT Id FROM Song WHERE LyricsSourceMusicId=Music.Id AND MergedIntoSongId IS NULL ORDER BY Id DESC LIMIT 1) " +
                "WHERE SongId=0 OR SongId IS NULL");
            db.Execute("UPDATE PlayListMusic SET SongId=(SELECT SongId FROM Music WHERE Id=PlayListMusic.MusicId) WHERE SongId IS NULL OR SongId=0");
            db.Execute("CREATE INDEX IF NOT EXISTS IX_Music_SongId ON Music(SongId)");
            db.Execute("CREATE INDEX IF NOT EXISTS IX_Playlist_EntryOrder ON PlayListMusic(PlayListId,[Order],Id)");
            db.Execute("CREATE INDEX IF NOT EXISTS IX_Playlist_SongId ON PlayListMusic(SongId)");
            db.Execute("UPDATE VersionLyricsOverride SET SourceKind=COALESCE(SourceKind,'VersionOverride'),SourceKey=COALESCE(SourceKey,''),Diagnostic=COALESCE(Diagnostic,'') WHERE SourceKind IS NULL OR SourceKey IS NULL OR Diagnostic IS NULL");
            db.Execute("CREATE TRIGGER IF NOT EXISTS SongAutoLink_Insert AFTER INSERT ON Song BEGIN INSERT OR IGNORE INTO SongAutoLinkDirty(SongId) VALUES(NEW.Id); END");
            db.Execute("CREATE TRIGGER IF NOT EXISTS SongAutoLink_Update AFTER UPDATE OF Title,Artist,Album ON Song " +
                "WHEN OLD.Title IS NOT NEW.Title OR OLD.Artist IS NOT NEW.Artist OR OLD.Album IS NOT NEW.Album " +
                "BEGIN INSERT OR IGNORE INTO SongAutoLinkDirty(SongId) VALUES(NEW.Id); END");
            db.Execute("CREATE TABLE IF NOT EXISTS VersionMatch(MusicId INTEGER PRIMARY KEY, MatchKey TEXT NOT NULL)");
            db.Execute("CREATE INDEX IF NOT EXISTS IX_VersionMatch_Key ON VersionMatch(MatchKey)");
            db.Execute("""
                CREATE TRIGGER IF NOT EXISTS SongAutoLink_Member AFTER UPDATE OF Title,Author,Album,TitleFromFileName,SongId,IsDetached ON Music
                WHEN OLD.Title IS NOT NEW.Title OR OLD.Author IS NOT NEW.Author OR OLD.Album IS NOT NEW.Album
                    OR OLD.TitleFromFileName IS NOT NEW.TitleFromFileName OR OLD.SongId IS NOT NEW.SongId OR OLD.IsDetached IS NOT NEW.IsDetached
                BEGIN
                    INSERT OR IGNORE INTO SongAutoLinkDirty SELECT OLD.SongId WHERE OLD.SongId>0;
                    INSERT OR IGNORE INTO SongAutoLinkDirty SELECT NEW.SongId WHERE NEW.SongId>0;
                END
                """);
            if (schema is null || schema.Version < 3)
            {
                migrateSourceScopes?.Invoke(db);
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WebDavSource'") != 0)
                    db.Execute("UPDATE Music SET IsDetached=1,IsMissing=1 WHERE SourceId>0 AND NOT EXISTS(SELECT 1 FROM WebDavSource s WHERE s.Id=Music.SourceId)");
                db.Execute("INSERT OR IGNORE INTO SongAutoLinkDirty(SongId) SELECT Id FROM Song WHERE MergedIntoSongId IS NULL");
                if (schema is null || schema.Version < 2) RestoreAutomaticExclusions(db);
                db.InsertOrReplace(new SongVersionSchemaRecord());
            }
        });
        if (schema is null)
        {
            if (await database.ExecuteScalarAsync<string>("PRAGMA quick_check") != "ok") throw new InvalidOperationException("Song database integrity check failed.");
            await database.InsertOrReplaceAsync(new SongVersionSchemaRecord());
        }
    }

    /// <summary>为新版本创建稳定身份；自动关联由批次提交后的后台流程完成。</summary>
    public static void AttachInTransaction(SQLiteConnection db, Music music)
    {
        if (music.Id <= 0 || music.SongId > 0) return;
        var song = new SongRecord { Title = music.Title, Artist = music.Author, Album = music.Album,
            IsFavorite = music.IsFavorite, FavoriteOrder = music.Order, LyricsSourceMusicId = music.Id };
        db.Insert(song);
        music.SongId = song.Id;
        db.Execute("UPDATE Music SET SongId=? WHERE Id=?", song.Id, music.Id);
    }

    /// <summary>将共享状态投影到刚物化的版本；在发布 UI 前调用。</summary>
    public async Task DecorateAsync(IReadOnlyList<Music> versions)
    {
        if (versions.Count == 0) return;
        HashSet<int> songIds = [];
        HashSet<int> versionIds = [];
        foreach (var version in versions) { songIds.Add(version.SongId); versionIds.Add(version.Id); }
        var songs = await database.QueryAsync<SongRecord>($"SELECT Id,IsFavorite,FavoriteOrder,PreferredMusicId FROM Song WHERE Id IN ({string.Join(',', songIds)})");
        var overrides = await database.QueryScalarsAsync<int>($"SELECT MusicId FROM VersionLyricsOverride WHERE MusicId IN ({string.Join(',', versionIds)})");
        ApplySongState(versions, songs, overrides);
    }

    /// <summary>曲库和歌单在同一 SQLite 快照中读取。整库加载不构造包含所有 ID 的 SQL。</summary>
    public async Task<(List<Music> Music, List<PlayListMusic> Entries)> ReadLibraryAsync(bool includePlaylists)
    {
        List<Music> music = [];
        List<PlayListMusic> entries = [];
        await database.RunInTransactionAsync(db =>
        {
            music = db.Query<Music>("SELECT * FROM Music WHERE IsDetached=0 ORDER BY Title");
            var songs = db.Query<SongRecord>("SELECT Id,IsFavorite,FavoriteOrder,PreferredMusicId FROM Song WHERE MergedIntoSongId IS NULL AND (IsFavorite=1 OR FavoriteOrder<>0 OR PreferredMusicId IS NOT NULL)");
            ApplySongState(music, songs, db.QueryScalars<int>("SELECT MusicId FROM VersionLyricsOverride"));
            if (includePlaylists) entries = db.Table<PlayListMusic>().ToList();
        }).ConfigureAwait(false);
        return (music, entries);
    }

    private static void ApplySongState(IReadOnlyList<Music> versions, List<SongRecord> songs, List<int> overrides)
    {
        Dictionary<int, SongRecord> byId = new(songs.Count);
        foreach (var song in songs) byId.Add(song.Id, song);
        HashSet<int> overridden = new(overrides);
        for (int i = 0; i < versions.Count; i++)
        {
            var music = versions[i];
            byId.TryGetValue(music.SongId, out var song);
            music.IsFavorite = song?.IsFavorite ?? false;
            music.Order = song?.FavoriteOrder ?? 0;
            music.SongPreferredMusicId = song?.PreferredMusicId;
            music.HasVersionLyrics = overridden.Contains(music.Id);
        }
    }

    /// <summary>获取某版本的歌曲。</summary>
    public Task<SongRecord?> GetSongAsync(int musicId) => database.FindWithQueryAsync<SongRecord>(
        "SELECT s.* FROM Song s JOIN Music m ON m.SongId=s.Id WHERE m.Id=?", musicId)!;

    /// <summary>获取全部仍在组内的版本，包含暂时缺失文件。</summary>
    public async Task<List<Music>> GetVersionsAsync(int musicId)
    {
        var versions = await database.QueryAsync<Music>("SELECT * FROM Music WHERE SongId=(SELECT SongId FROM Music WHERE Id=?) AND IsDetached=0 ORDER BY Id", musicId);
        await DecorateAsync(versions);
        return versions;
    }

    /// <summary>仅生成三项一致的其他歌曲候选。</summary>
    public Task<List<Music>> GetCandidatesAsync(Music music) => database.QueryAsync<Music>(CandidateQuery, music.Id, music.Id);

    internal const string CandidateQuery = """
        SELECT * FROM Music WHERE Id IN (
            SELECT MIN(m.Id) FROM VersionMatch v INDEXED BY IX_VersionMatch_Key JOIN Music m ON m.Id=v.MusicId
            WHERE v.MatchKey=(SELECT MatchKey FROM VersionMatch WHERE MusicId=?)
              AND m.IsDetached=0 AND m.SongId<>(SELECT SongId FROM Music WHERE Id=?)
            GROUP BY m.SongId
        ) ORDER BY Id
        """;

    /// <summary>保存手动首选或自动模式，不触碰播放器。</summary>
    public Task SetPreferredAsync(int musicId, int? preferred, CancellationToken token = default) => database.RunInTransactionAsync(db =>
    {
        token.ThrowIfCancellationRequested();
        var music = db.Find<Music>(musicId) ?? throw Conflict();
        if (preferred is int id && db.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE Id=? AND SongId=? AND IsDetached=0", id, music.SongId) != 1)
            throw Conflict();
        db.Execute("UPDATE Song SET PreferredMusicId=?,Revision=Revision+1 WHERE Id=?", preferred, music.SongId);
    });

    /// <summary>创建带完整恢复资料的合并预览。</summary>
    public async Task<SongMergePreview> PreviewMergeAsync(int targetMusicId, int candidateMusicId, LyricsRepository lyrics, CancellationToken token = default)
    {
        var target = await database.FindAsync<Music>(targetMusicId) ?? throw Conflict();
        var candidate = await database.FindAsync<Music>(candidateMusicId) ?? throw Conflict();
        if (target.SongId == candidate.SongId || !SongVersionPolicy.Matches(target, candidate)) throw Conflict();
        await lyrics.GetSharedAsync(targetMusicId, token);
        await lyrics.GetSharedAsync(candidateMusicId, token);
        SongVersionSnapshot? snapshot = null;
        await database.RunInTransactionAsync(db => snapshot = Capture(db, [target.SongId, candidate.SongId]));
        return new(target.SongId, candidate.SongId, targetMusicId, candidateMusicId, snapshot!);
    }

    /// <summary>提交用户检查过的预览；各文件 info 和封面保持原样。</summary>
    public Task MergeAsync(SongMergePreview preview, int lyricsSongId, bool collapseDuplicates, CancellationToken token = default)
        => database.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            int[] ids = [preview.TargetSongId, preview.OtherSongId];
            if (Serialize(Capture(db, ids)) != Serialize(preview.State)) throw Conflict();
            var targetMusic = db.Find<Music>(preview.TargetMusicId) ?? throw Conflict();
            var otherMusic = db.Find<Music>(preview.OtherMusicId) ?? throw Conflict();
            if (!SongVersionPolicy.Matches(targetMusic, otherMusic)) throw Conflict();
            var target = db.Find<SongRecord>(preview.TargetSongId) ?? throw Conflict();
            var other = db.Find<SongRecord>(preview.OtherSongId) ?? throw Conflict();
            var targetLyrics = db.Find<SongLyricsRecord>(target.Id) ?? throw Conflict();
            var otherLyrics = db.Find<SongLyricsRecord>(other.Id) ?? throw Conflict();
            if (lyricsSongId != target.Id && lyricsSongId != other.Id) throw Conflict();
            var chosen = lyricsSongId == target.Id ? targetLyrics : otherLyrics;
            var notChosen = lyricsSongId == target.Id ? otherLyrics : targetLyrics;
            bool different = chosen.Snapshot().Document != notChosen.Snapshot().Document;
            // 无法证明恒定偏移时保留原时间轴；用户可显式切换回共享歌词。
            if (different)
                foreach (var music in db.Query<Music>("SELECT * FROM Music WHERE SongId=?", notChosen.SongId))
                    if (db.Find<VersionLyricsOverrideRecord>(music.Id) is null)
                        db.Insert(new VersionLyricsOverrideRecord { MusicId = music.Id, Lyrics = notChosen.Lyrics,
                            LyricsFormat = notChosen.LyricsFormat, TranslatedLyrics = notChosen.TranslatedLyrics,
                            PronunciationLyrics = notChosen.PronunciationLyrics, Revision = notChosen.Revision + 1,
                            SourceKind = notChosen.SourceKind, SourceKey = notChosen.SourceKey, Diagnostic = notChosen.Diagnostic });
            var shared = SongLyricsRecord.From(target.Id, chosen.Snapshot());
            shared.Revision = Math.Max(targetLyrics.Revision, otherLyrics.Revision) + 1;
            db.InsertOrReplace(shared);
            target.IsFavorite |= other.IsFavorite;
            target.FavoriteOrder = Math.Max(target.FavoriteOrder, other.FavoriteOrder);
            target.Revision++;
            db.Update(target);
            other.MergedIntoSongId = target.Id;
            other.Revision++;
            db.Update(other);
            db.Execute("UPDATE Music SET SongId=?,IsFavorite=?,[Order]=? WHERE SongId IN (?,?)",
                target.Id, target.IsFavorite, target.FavoriteOrder, target.Id, other.Id);
            db.Execute("UPDATE PlayListMusic SET SongId=? WHERE SongId=?", target.Id, other.Id);
            db.Delete<SongAutoLinkRecord>(other.Id);
            db.InsertOrReplace(new SongAutoLinkRecord { SongId = target.Id, MatchKey = SongVersionPolicy.GetMatchKey(target.Title, target.Artist, target.Album) });
            if (collapseDuplicates)
            {
                var entries = db.Query<PlayListMusic>("SELECT * FROM PlayListMusic WHERE SongId=? ORDER BY PlayListId,[Order] DESC,Id", target.Id);
                HashSet<int> playlists = [];
                foreach (var entry in entries) if (!playlists.Add(entry.PlayListId)) db.Delete(entry);
            }
            IndexMemberTags(db, target.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
            SaveOperation(db, target.Id, "Merge", preview.State, Capture(db, ids));
        });

    /// <summary>拆出指定文件，复制共享用户资料，固定条目随文件移动。</summary>
    public async Task SplitAsync(int musicId, LyricsRepository lyrics, CancellationToken token = default)
    {
        await lyrics.GetSharedAsync(musicId, token);
        await database.RunInTransactionAsync(db =>
        {
            token.ThrowIfCancellationRequested();
            var music = db.Find<Music>(musicId) ?? throw Conflict();
            int originalId = music.SongId;
            if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE SongId=? AND IsDetached=0", originalId) < 2) throw Conflict();
            var before = Capture(db, [originalId]);
            var original = db.Find<SongRecord>(originalId) ?? throw Conflict();
            var created = new SongRecord { Title = music.Title, Artist = music.Author, Album = music.Album,
                IsFavorite = original.IsFavorite, FavoriteOrder = original.FavoriteOrder, LyricsSourceMusicId = music.Id };
            db.Insert(created);
            db.InsertOrReplace(new SongAutoLinkRecord { SongId = created.Id, IsExcluded = true });
            var own = db.Find<VersionLyricsOverrideRecord>(music.Id);
            var document = own?.Snapshot() ?? db.Find<SongLyricsRecord>(originalId)!.Snapshot();
            var copied = SongLyricsRecord.From(created.Id, document);
            copied.Revision++;
            db.Insert(copied);
            db.Delete<VersionLyricsOverrideRecord>(music.Id);
            db.Execute("UPDATE Music SET SongId=? WHERE Id=?", created.Id, music.Id);
            db.Execute("UPDATE PlayListMusic SET SongId=? WHERE RequestedMusicId=?", created.Id, music.Id);
            if (original.PreferredMusicId == musicId) original.PreferredMusicId = null;
            original.Revision++;
            db.Update(original);
            IndexMemberTags(db, $"{originalId},{created.Id}", token);
            SaveOperation(db, originalId, "Split", before, Capture(db, [originalId, created.Id]));
        });
    }

    /// <summary>查询最后一次尚未撤销的相关操作。</summary>
    public Task<SongVersionOperationRecord?> GetLastOperationAsync(int songId) => database.FindWithQueryAsync<SongVersionOperationRecord>(
        "SELECT * FROM SongVersionOperation WHERE (SongId=? OR RelatedSongId=?) AND Undone=0 ORDER BY Id DESC LIMIT 1", songId, songId)!;

    /// <summary>仅在后状态未被修改时恢复；拒绝覆盖后续用户工作。</summary>
    public Task UndoAsync(int operationId, CancellationToken token = default) => database.RunInTransactionAsync(db =>
    {
        token.ThrowIfCancellationRequested();
        var operation = db.Find<SongVersionOperationRecord>(operationId) ?? throw Conflict();
        if (operation.Undone) return;
        var before = JsonSerializer.Deserialize(operation.BeforeJson, SongVersionJsonContext.Default.SongVersionSnapshot) ?? throw Conflict();
        var after = JsonSerializer.Deserialize(operation.AfterJson, SongVersionJsonContext.Default.SongVersionSnapshot) ?? throw Conflict();
        int[] songIds = new int[after.Songs.Count];
        for (int i = 0; i < songIds.Length; i++) songIds[i] = after.Songs[i].Id;
        if (Serialize(Capture(db, songIds)) != Serialize(after)) throw new InvalidOperationException("SongVersionsRecoveryConflict");
        foreach (var row in after.Playlists) db.Delete<PlayListMusic>(row.Id);
        foreach (var row in after.Overrides) db.Delete<VersionLyricsOverrideRecord>(row.MusicId);
        foreach (var row in after.Lyrics) db.Delete<SongLyricsRecord>(row.SongId);
        foreach (var row in after.Songs) db.Delete<SongRecord>(row.Id);
        foreach (var row in before.Songs) db.InsertOrReplace(row);
        foreach (var row in before.Versions)
            db.Execute("UPDATE Music SET SongId=?,IsFavorite=?,[Order]=?,LyricsOffsetMs=? WHERE Id=?", row.SongId, row.IsFavorite, row.Order, row.Offset, row.Id);
        foreach (var row in before.Playlists) db.InsertOrReplace(row);
        foreach (var row in before.Lyrics) db.Insert(row);
        foreach (var row in before.Overrides) db.Insert(row);
        foreach (var row in after.Songs)
            if (!before.Songs.Exists(song => song.Id == row.Id)) db.Delete<SongAutoLinkRecord>(row.Id);
        foreach (var row in before.Songs)
        {
            if (operation.Kind is "Merge" or "AutoMerge")
                db.InsertOrReplace(new SongAutoLinkRecord { SongId = row.Id, IsExcluded = true });
            db.InsertOrReplace(new SongAutoLinkDirtyRecord { SongId = row.Id });
        }
        operation.Undone = true;
        db.Update(operation);
    });

    /// <summary>显式改为共享歌词；旧覆盖保留在操作快照中。</summary>
    public Task UseSharedLyricsAsync(int musicId) => database.RunInTransactionAsync(db =>
    {
        var music = db.Find<Music>(musicId) ?? throw Conflict();
        var before = Capture(db, [music.SongId]);
        db.Delete<VersionLyricsOverrideRecord>(musicId);
        db.Execute("UPDATE Song SET Revision=Revision+1 WHERE Id=?", music.SongId);
        SaveOperation(db, music.SongId, "SharedLyrics", before, Capture(db, [music.SongId]));
    });

    private static void SaveOperation(SQLiteConnection db, int songId, string kind, SongVersionSnapshot before, SongVersionSnapshot after)
    {
        int other = 0;
        foreach (var song in after.Songs) if (song.Id != songId) { other = song.Id; break; }
        db.Insert(new SongVersionOperationRecord { SongId = songId, RelatedSongId = other, Kind = kind, BeforeJson = Serialize(before), AfterJson = Serialize(after) });
    }

    private static SongVersionSnapshot Capture(SQLiteConnection db, int[] ids)
    {
        Array.Sort(ids);
        string parameters = string.Join(',', ids); // 仅内部整数 ID，不拼接用户文本。
        return new()
        {
            Songs = db.Query<SongRecord>($"SELECT * FROM Song WHERE Id IN ({parameters}) ORDER BY Id"),
            Versions = db.Query<SongVersionMembership>($"SELECT Id,SongId,IsFavorite,[Order],LyricsOffsetMs AS Offset,MetadataRevision,Path,IsMissing,IsDetached FROM Music WHERE SongId IN ({parameters}) ORDER BY Id"),
            Playlists = db.Query<PlayListMusic>($"SELECT * FROM PlayListMusic WHERE SongId IN ({parameters}) ORDER BY Id"),
            Lyrics = db.Query<SongLyricsRecord>($"SELECT * FROM SongLyrics WHERE SongId IN ({parameters}) ORDER BY SongId"),
            Overrides = db.Query<VersionLyricsOverrideRecord>($"SELECT v.* FROM VersionLyricsOverride v JOIN Music m ON v.MusicId=m.Id WHERE m.SongId IN ({parameters}) ORDER BY v.MusicId")
        };
    }

    private static string Serialize(SongVersionSnapshot snapshot) => JsonSerializer.Serialize(snapshot, SongVersionJsonContext.Default.SongVersionSnapshot);
    private static InvalidOperationException Conflict() => new("SongVersionsConflict");
}

/// <summary>保存不包含音频及封面字节的操作恢复资料。</summary>
public sealed class SongVersionSnapshot
{
    /// <summary>获取或设置歌曲状态。</summary>
    public List<SongRecord> Songs { get; set; } = [];
    /// <summary>获取或设置版本归属。</summary>
    public List<SongVersionMembership> Versions { get; set; } = [];
    /// <summary>获取或设置受影响出现项。</summary>
    public List<PlayListMusic> Playlists { get; set; } = [];
    /// <summary>获取或设置共享歌词。</summary>
    public List<SongLyricsRecord> Lyrics { get; set; } = [];
    /// <summary>获取或设置独立歌词。</summary>
    public List<VersionLyricsOverrideRecord> Overrides { get; set; } = [];
}

/// <summary>保存操作会改变的版本列及并发验证信息。</summary>
public sealed class SongVersionMembership
{
    /// <summary>获取或设置版本标识。</summary>
    public int Id { get; set; }
    /// <summary>获取或设置歌曲标识。</summary>
    public int SongId { get; set; }
    /// <summary>获取或设置收藏投影。</summary>
    public bool IsFavorite { get; set; }
    /// <summary>获取或设置收藏排序。</summary>
    public int Order { get; set; }
    /// <summary>获取或设置版本偏移。</summary>
    public int Offset { get; set; }
    /// <summary>获取或设置标签并发修订。</summary>
    public long MetadataRevision { get; set; }
    /// <summary>获取或设置验证路径。</summary>
    public string Path { get; set; } = "";
    /// <summary>获取或设置预览时的缺失状态。</summary>
    public bool IsMissing { get; set; }
    /// <summary>获取或设置预览时的移除状态。</summary>
    public bool IsDetached { get; set; }
}

/// <summary>提供用户检查的不可变预览入口；提交时再次检查整个状态。</summary>
public sealed record SongMergePreview(int TargetSongId, int OtherSongId, int TargetMusicId, int OtherMusicId, SongVersionSnapshot State);

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(SongVersionSnapshot))]
internal sealed partial class SongVersionJsonContext : JsonSerializerContext;
