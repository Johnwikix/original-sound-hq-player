using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

/// <summary>One authoritative row per library track. Legacy data is read-only recovery data.</summary>
public sealed class LyricsRepository(SQLiteAsyncConnection database, LyricsParser parser)
{
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
        await database.CreateTableAsync<LyricsSearchStateRecord>();
    }

    public async Task<LyricsSnapshot> GetAsync(int musicId, CancellationToken token = default)
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
        string sourceKind, string sourceKey = "", CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return await database.ExecuteAsync(
            "UPDATE MusicLyricsV2 SET Lyrics=?, LyricsFormat=?, TranslatedLyrics=?, SourceKind=?, SourceKey=?, Revision=Revision+1, Diagnostic='' " +
            "WHERE SchemaVersion=2 AND MusicId=? AND Revision=? AND EXISTS(SELECT 1 FROM Music WHERE Id=?)",
            document.Original.Content, (int)document.Original.Format, document.TranslationLrc, sourceKind, sourceKey,
            musicId, expectedRevision, musicId) == 1;
    }

    /// <summary>Runs in the metadata transaction so queueing and editor revision checks commit together.</summary>
    public static void SaveInTransaction(SQLiteConnection db, int musicId, LyricsDocument document, long expectedRevision, string kind, bool lyricsChanged = true)
    {
        // Metadata edits still advance the editor revision, but retain lyric provenance and diagnostics.
        int changed = lyricsChanged ? db.Execute("UPDATE MusicLyricsV2 SET Lyrics=?, LyricsFormat=?, TranslatedLyrics=?, SourceKind=?, SourceKey='', " +
            "Revision=Revision+1, Diagnostic='' WHERE SchemaVersion=2 AND MusicId=? AND Revision=? AND EXISTS(SELECT 1 FROM Music WHERE Id=?)",
            document.Original.Content, (int)document.Original.Format, document.TranslationLrc, kind, musicId, expectedRevision, musicId)
            : db.Execute("UPDATE MusicLyricsV2 SET Revision=Revision+1 WHERE SchemaVersion=2 AND MusicId=? AND Revision=? " +
                "AND EXISTS(SELECT 1 FROM Music WHERE Id=?)", musicId, expectedRevision, musicId);
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
                try { await GetAsync(id, token); }
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
            LyricsFormat = document.Original.Format, TranslatedLyrics = document.TranslationLrc, SourceKind = "Embedded",
            MigratedFromHash = LyricsLegacyMigration.Fingerprint(legacy) });
    }
}
