using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>One authoritative row per library track. Legacy data is read-only recovery data.</summary>
public sealed class LyricsRepository(SQLiteAsyncConnection database, LyricsParser parser)
{
    private bool _hasSongs;
    private sealed class TableColumnInfo { public string Name { get; set; } = ""; }
    public async Task InitializeAsync(string databasePath)
    {
        bool exists = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='MusicLyricsV2'") != 0;
        bool legacyQueue = !exists && await database.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingMetadataWrite'") > 0 &&
            await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM PendingMetadataWrite") > 0;
        if (!exists && (legacyQueue || await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM MusicLyrics") > 0))
        {
            string backup = databasePath + ".lyrics-v1.bak";
            if (!File.Exists(backup))
            {
                string temporary = backup + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await database.BackupAsync(temporary);
                    File.Move(temporary, backup, false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        await database.CreateTableAsync<MusicLyricsRecord>();
        var columns = await database.QueryAsync<TableColumnInfo>("PRAGMA table_info(MusicLyricsV2)");
        if (!columns.Any(column => string.Equals(column.Name, nameof(MusicLyricsRecord.PronunciationLyrics), StringComparison.OrdinalIgnoreCase)))
            await database.ExecuteAsync("ALTER TABLE MusicLyricsV2 ADD COLUMN PronunciationLyrics TEXT NULL");
        await database.CreateTableAsync<LyricsSearchStateRecord>();
        _hasSongs = await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Song'") != 0;
    }

    public async Task<LyricsSnapshot> GetAsync(int musicId, CancellationToken token = default)
    {
        if (_hasSongs && await database.FindAsync<VersionLyricsOverrideRecord>(musicId) is { } own) return own.Snapshot();
        return await GetSharedAsync(musicId, token);
    }

    /// <summary>按有界批次迁移自动关联所需的默认歌词，避免每个候选多次异步查询。</summary>
    public async Task EnsureSharedForSongsAsync(IReadOnlyList<int> songIds, CancellationToken token = default)
    {
        for (int start = 0; start < songIds.Count; start += 256)
        {
            token.ThrowIfCancellationRequested();
            var ids = new List<int>(Math.Min(256, songIds.Count - start));
            for (int i = start; i < Math.Min(start + 256, songIds.Count); i++) ids.Add(songIds[i]);
            var missing = await database.QueryAsync<SongRecord>($"SELECT s.* FROM Song s WHERE s.Id IN ({string.Join(',', ids)}) " +
                "AND NOT EXISTS(SELECT 1 FROM SongLyrics WHERE SongId=s.Id)").ConfigureAwait(false);
            if (missing.Count == 0) continue;
            // 来源身份由建组时保留；硬删除过来源的旧库从仍存在的版本恢复。
            await database.RunInTransactionAsync(db =>
            {
                foreach (var song in missing)
                    if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE Id=?", song.LyricsSourceMusicId) == 0)
                        song.LyricsSourceMusicId = db.ExecuteScalar<int>("SELECT MIN(Id) FROM Music WHERE SongId=?", song.Id);
            }).ConfigureAwait(false);
            List<int> sources = new(missing.Count);
            foreach (var song in missing) if (song.LyricsSourceMusicId > 0) sources.Add(song.LyricsSourceMusicId);
            if (sources.Count == 0) continue;
            string numbers = string.Join(',', sources);
            var records = await database.QueryAsync<MusicLyricsRecord>($"SELECT * FROM MusicLyricsV2 WHERE MusicId IN ({numbers})").ConfigureAwait(false);
            var legacy = await database.QueryAsync<MusicLyrics>($"SELECT * FROM MusicLyrics WHERE MusicId IN ({numbers})").ConfigureAwait(false);
            Dictionary<int, MusicLyricsRecord> documents = new(records.Count);
            Dictionary<int, MusicLyrics> originals = new(legacy.Count);
            foreach (var row in records) documents[row.MusicId] = row;
            foreach (var row in legacy) originals[row.MusicId] = row;
            await Task.Run(() =>
            {
                foreach (int id in sources)
                {
                    token.ThrowIfCancellationRequested();
                    if (documents.TryGetValue(id, out var existing))
                    {
                        if (existing.SchemaVersion != 2) throw new InvalidOperationException("Unsupported lyrics schema version.");
                        continue;
                    }
                    originals.TryGetValue(id, out var old);
                    var converted = LyricsLegacyMigration.Convert(old, parser, token);
                    converted.MusicId = id;
                    documents.Add(id, converted);
                }
            }, token).ConfigureAwait(false);
            await database.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                foreach (var song in missing)
                {
                    if (song.LyricsSourceMusicId <= 0) continue;
                    var current = db.Find<MusicLyricsRecord>(song.LyricsSourceMusicId);
                    if (current is null)
                    {
                        current = documents[song.LyricsSourceMusicId];
                        if (LyricsLegacyMigration.Fingerprint(db.Find<MusicLyrics>(current.MusicId)) != current.MigratedFromHash) continue;
                        db.Insert(current, "OR IGNORE");
                    }
                    if (current.SchemaVersion != 2) throw new InvalidOperationException("Unsupported lyrics schema version.");
                    db.Insert(SongLyricsRecord.From(song.Id, current.Snapshot()), "OR IGNORE");
                }
            }).ConfigureAwait(false);
        }
    }

    /// <summary>获取组内默认文档，不使用该版本的独立覆盖。</summary>
    public async Task<LyricsSnapshot> GetSharedAsync(int musicId, CancellationToken token = default)
    {
        if (!_hasSongs) return await GetVersionAsync(musicId, token);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var song = await database.FindWithQueryAsync<SongRecord>("SELECT s.* FROM Song s JOIN Music m ON m.SongId=s.Id WHERE m.Id=?", musicId);
            if (song is null) return await GetVersionAsync(musicId, token);
            if (await database.FindAsync<SongLyricsRecord>(song.Id) is { } current) return current.Snapshot();
            var legacy = await GetVersionAsync(song.LyricsSourceMusicId > 0 ? song.LyricsSourceMusicId : musicId, token);
            bool inserted = false;
            await database.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                if (db.ExecuteScalar<int>("SELECT SongId FROM Music WHERE Id=?", musicId) != song.Id) return;
                if (db.Find<SongLyricsRecord>(song.Id) is null) db.Insert(SongLyricsRecord.From(song.Id, legacy));
                inserted = true;
            });
            if (inserted) return (await database.FindAsync<SongLyricsRecord>(song.Id)).Snapshot();
        }
    }

    private async Task<LyricsSnapshot> GetVersionAsync(int musicId, CancellationToken token = default)
    {
        if (musicId <= 0) throw new ArgumentOutOfRangeException(nameof(musicId));
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var current = await database.FindAsync<MusicLyricsRecord>(musicId);
            if (current is not null)
            {
                if (current.SchemaVersion != 2) throw new InvalidOperationException("Unsupported lyrics schema version.");
                var old = await database.FindAsync<MusicLyrics>(musicId);
                if (old is not null && LyricsLegacyMigration.Fingerprint(old) != current.MigratedFromHash)
                    current.Diagnostic = "LegacyChangedAfterUpgrade";
                return current.Snapshot();
            }
            var legacy = await database.FindAsync<MusicLyrics>(musicId);
            var converted = await Task.Run(() => LyricsLegacyMigration.Convert(legacy, parser, token), token);
            converted.MusicId = musicId;
            bool inserted = false;
            await database.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE Id=?", musicId) == 0) return;
                if (db.Find<MusicLyricsRecord>(musicId) is not null) return;
                if (LyricsLegacyMigration.Fingerprint(db.Find<MusicLyrics>(musicId)) != converted.MigratedFromHash) return;
                db.Insert(converted);
                inserted = true;
            });
            if (inserted) return converted.Snapshot();
            if (await database.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Music WHERE Id=?", musicId) == 0)
                throw new InvalidOperationException("The track no longer exists.");
        }
    }

    public async Task<bool> SaveAsync(int musicId, LyricsDocument document, long expectedRevision,
        string sourceKind, string sourceKey = "", CancellationToken token = default, int expectedOwnerId = 0)
    {
        token.ThrowIfCancellationRequested();
        if (_hasSongs)
        {
            bool saved = false;
            await database.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                try { SaveInTransaction(db, musicId, document, expectedRevision, sourceKind, true, expectedOwnerId, sourceKey); saved = true; }
                catch (InvalidOperationException ex) when (ex.Message == "LyricsEditConflict") { }
            });
            return saved;
        }
        return await database.ExecuteAsync(
            "UPDATE MusicLyricsV2 SET Lyrics=?, LyricsFormat=?, TranslatedLyrics=?, PronunciationLyrics=?, SourceKind=?, SourceKey=?, Revision=Revision+1, Diagnostic='' " +
            "WHERE SchemaVersion=2 AND MusicId=? AND Revision=? AND EXISTS(SELECT 1 FROM Music WHERE Id=?)",
            document.Original.Content, (int)document.Original.Format, document.TranslationLrc, document.PronunciationLrc, sourceKind, sourceKey,
            musicId, expectedRevision, musicId) == 1;
    }

    /// <summary>Runs in the metadata transaction so queueing and editor revision checks commit together.</summary>
    public static void SaveInTransaction(SQLiteConnection db, int musicId, LyricsDocument document, long expectedRevision, string kind, bool lyricsChanged = true,
        int expectedOwnerId = 0, string sourceKey = "")
    {
        if (!lyricsChanged) return; // 文件标签编辑不抢占共享歌词修订。
        if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='Song'") != 0)
        {
            int songId = db.ExecuteScalar<int>("SELECT SongId FROM Music WHERE Id=?", musicId);
            bool own = db.Find<VersionLyricsOverrideRecord>(musicId) is not null;
            int owner = own ? -musicId : songId;
            if (expectedOwnerId != 0 && expectedOwnerId != owner) throw new InvalidOperationException("LyricsEditConflict");
            int updated = own
                ? db.Execute("UPDATE VersionLyricsOverride SET Lyrics=?,LyricsFormat=?,TranslatedLyrics=?,PronunciationLyrics=?,SourceKind=?,SourceKey=?,Diagnostic='',Revision=Revision+1 WHERE MusicId=? AND Revision=?",
                    document.Original.Content, (int)document.Original.Format, document.TranslationLrc, document.PronunciationLrc, kind, sourceKey, musicId, expectedRevision)
                : db.Execute("UPDATE SongLyrics SET Lyrics=?,LyricsFormat=?,TranslatedLyrics=?,PronunciationLyrics=?,SourceKind=?,SourceKey=?,Revision=Revision+1,Diagnostic='' " +
                    "WHERE SongId=? AND Revision=? AND EXISTS(SELECT 1 FROM Music WHERE Id=? AND SongId=?)",
                    document.Original.Content, (int)document.Original.Format, document.TranslationLrc, document.PronunciationLrc, kind, sourceKey, songId, expectedRevision, musicId, songId);
            if (updated != 1) throw new InvalidOperationException("LyricsEditConflict");
            return;
        }
        int changed = db.Execute("UPDATE MusicLyricsV2 SET Lyrics=?, LyricsFormat=?, TranslatedLyrics=?, PronunciationLyrics=?, SourceKind=?, SourceKey='', " +
            "Revision=Revision+1, Diagnostic='' WHERE SchemaVersion=2 AND MusicId=? AND Revision=? AND EXISTS(SELECT 1 FROM Music WHERE Id=?)",
            document.Original.Content, (int)document.Original.Format, document.TranslationLrc, document.PronunciationLrc, kind, musicId, expectedRevision, musicId);
        if (changed != 1) throw new InvalidOperationException("LyricsEditConflict");
    }

    public async Task MigrateRemainingAsync(CancellationToken token)
    {
        int after = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var ids = await database.QueryScalarsAsync<int>("SELECT l.MusicId FROM MusicLyrics l JOIN Music m ON m.Id=l.MusicId " +
                "LEFT JOIN MusicLyricsV2 n ON n.MusicId=l.MusicId WHERE n.MusicId IS NULL AND l.MusicId>? ORDER BY l.MusicId LIMIT 32", after);
            if (ids.Count == 0) return;
            foreach (int id in ids)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    // 各旧文件文档先单独归档为 V2，避免共享文档已存在时每次启动都重复迁移。
                    await GetVersionAsync(id, token);
                    if (_hasSongs) await GetSharedAsync(id, token);
                }
                catch (InvalidOperationException) { /* A concurrently removed track has no migration work. */ }
                after = id;
            }
            await Task.Delay(20, token);
        }
    }

    public async Task<LyricsSearchStateRecord?> GetSearchAsync(int id, string channel) => await database.FindAsync<LyricsSearchStateRecord>($"{id}:{channel}");

    public Task SetSearchAsync(int id, string channel, string hash, string status) => database.ExecuteAsync(
        "INSERT OR REPLACE INTO LyricsSearchState(Key,MusicId,Channel,QueryHash,Status) SELECT ?,?,?,?,? WHERE EXISTS(SELECT 1 FROM Music WHERE Id=?)",
        $"{id}:{channel}", id, channel, hash, status, id);

    public static void DeleteInTransaction(SQLiteConnection db, int id)
    {
        db.Delete<MusicLyricsRecord>(id);
        db.Execute("DELETE FROM LyricsSearchState WHERE MusicId=?", id);
        db.Delete<MusicLyrics>(id);
    }

    /// <summary>Scanner input is already normalized before entering its short write transaction.</summary>
    public static void AddEmbeddedInTransaction(SQLiteConnection db, int id, LyricsDocument document)
    {
        if (db.Find<MusicLyricsRecord>(id) is not null) return; // An explicitly cleared row is authoritative too.
        var legacy = db.Find<MusicLyrics>(id);
        if (legacy is not null && (!string.IsNullOrWhiteSpace(legacy.Lyrics) || !string.IsNullOrWhiteSpace(legacy.Krc) ||
            !string.IsNullOrWhiteSpace(legacy.TranslatedLyrics) || !string.IsNullOrWhiteSpace(legacy.TKrc))) return;
        db.Insert(new MusicLyricsRecord { MusicId = id, Lyrics = document.Original.Content,
            LyricsFormat = document.Original.Format, TranslatedLyrics = document.TranslationLrc, PronunciationLyrics = document.PronunciationLrc, SourceKind = "Embedded",
            MigratedFromHash = LyricsLegacyMigration.Fingerprint(legacy) });
    }
}
