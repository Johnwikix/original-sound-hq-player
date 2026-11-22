using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

internal static class MigrationBackupChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string local = Directory.CreateDirectory(Path.Combine(root, "package")).FullName;
        string docs = Path.Combine(root, "documents");
        string legacy = Path.Combine(local, LibraryDatabaseMigration.LegacyFile);
        using (var writer = new SQLiteConnection(legacy))
        {
            writer.EnableWriteAheadLogging();
            writer.ExecuteScalar<int>("PRAGMA wal_autocheckpoint=0");
            writer.Execute("CREATE TABLE Music(Id INTEGER PRIMARY KEY, Title TEXT)");
            writer.ExecuteScalar<int>("PRAGMA wal_checkpoint(TRUNCATE)");
            writer.Execute("INSERT INTO Music VALUES(41,'before')");
            writer.BeginTransaction();
            writer.Execute("INSERT INTO Music VALUES(42,'uncommitted')");
            check(new FileInfo(legacy + "-wal").Length > 0, "legacy fixture has uncheckpointed WAL frames");
            string backup = "";
            var migrated = await LibraryDatabaseMigration.OpenAsync(docs, local, async (db, upgrading) =>
            {
                using var saved = new SQLiteConnection(backup, SQLiteOpenFlags.ReadOnly);
                check(upgrading && saved.ExecuteScalar<string>("SELECT Title FROM Music") == "before" &&
                    saved.ExecuteScalar<int>("SELECT COUNT(*) FROM Music") == 1, "backup precedes first write and includes only committed WAL data");
                await db.ExecuteAsync("ALTER TABLE Music ADD COLUMN SongId INTEGER");
                await db.ExecuteAsync("UPDATE Music SET Title='after'");
            }, path => backup = path);
            await migrated.CloseAsync();
            writer.Rollback();
            check(writer.ExecuteScalar<string>("SELECT Title FROM Music") == "before" &&
                writer.ExecuteScalar<int>("SELECT COUNT(*) FROM pragma_table_info('Music') WHERE name='SongId'") == 0,
                "package legacy database retains its original rows and schema");
            var current = await LibraryDatabaseMigration.OpenAsync(docs, local, (db, upgrading) =>
            {
                check(!upgrading, "current generation reopens without migration");
                return Task.CompletedTask;
            }, _ => throw new Exception("Unexpected additional backup"));
            check(await current.ExecuteScalarAsync<string>("SELECT Title FROM Music") == "after", "active database wins over older source on restart");
            await current.CloseAsync();

            // 实际按恢复说明将单文件快照放进一个独立目录，再从正常入口升级。
            string restore = Directory.CreateDirectory(Path.Combine(root, "restore")).FullName;
            File.Copy(backup, Path.Combine(restore, LibraryDatabaseMigration.ActiveFile));
            var restored = await LibraryDatabaseMigration.OpenAsync(null, restore, async (db, upgrading) =>
            {
                check(upgrading && await db.ExecuteScalarAsync<string>("SELECT Title FROM Music") == "before" &&
                    await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM pragma_table_info('Music') WHERE name='SongId'") == 0,
                    "standalone backup restores pre-upgrade rows and columns without WAL sidecars");
            }, _ => { });
            await restored.CloseAsync();
        }

        foreach (int songVersion in new[] { 1, 2, 3 })
        {
            string directory = Directory.CreateDirectory(Path.Combine(root, "song-v" + songVersion)).FullName;
            string active = Path.Combine(directory, LibraryDatabaseMigration.ActiveFile);
            using (var source = new SQLiteConnection(active))
            {
                source.Execute("CREATE TABLE SongVersionSchema(Id INTEGER PRIMARY KEY, Version INTEGER)");
                source.Execute("INSERT INTO SongVersionSchema VALUES(1,?)", songVersion);
            }
            string? snapshot = null;
            try
            {
                await LibraryDatabaseMigration.OpenAsync(null, directory, async (db, _) =>
                {
                    await db.ExecuteAsync("CREATE TABLE FirstCommittedStep(Id INTEGER)");
                    throw new InvalidOperationException("injected migration failure");
                }, path => snapshot = path);
                throw new Exception("Expected migration failure");
            }
            catch (InvalidOperationException ex) when (ex.Message == "injected migration failure") { }
            check(snapshot is not null, $"song v{songVersion} has a baseline before schema changes");
            using (var saved = new SQLiteConnection(snapshot!, SQLiteOpenFlags.ReadOnly))
                check(saved.ExecuteScalar<int>("SELECT Version FROM SongVersionSchema") == songVersion &&
                    saved.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='FirstCommittedStep'") == 0,
                    $"song v{songVersion} snapshot retains the original schema after failure");
            if (songVersion == 1)
            {
                File.Move(snapshot!, snapshot + ".hold");
                await ExpectFailureAsync(directory, check, "interrupted migration stops if its original backup is missing");
                using (var replacement = new SQLiteConnection(snapshot!))
                    replacement.Execute("CREATE TABLE Unrelated(Id INTEGER PRIMARY KEY)");
                await ExpectFailureAsync(directory, check, "retry rejects a valid but unrelated database under the checkpoint filename");
                File.Delete(snapshot!);
                File.Move(snapshot + ".hold", snapshot!);
            }
            var retried = await LibraryDatabaseMigration.OpenAsync(Path.Combine(directory, "preferred"), directory, async (db, upgrading) =>
            {
                check(upgrading && await db.ExecuteScalarAsync<int>("PRAGMA user_version") == 0, "failed migration never commits the generation");
            }, path => check(path == snapshot, "retry validates and reuses the original checkpoint"));
            check(retried.DatabasePath == active && await retried.ExecuteScalarAsync<int>("PRAGMA user_version") == LibraryDatabaseMigration.CurrentVersion &&
                Directory.GetFiles(Path.Combine(directory, "Backups")).Length == 1, "retry completes on authoritative database with one backup");
            await retried.CloseAsync();
        }

        string blocked = Directory.CreateDirectory(Path.Combine(root, "blocked")).FullName;
        string blockedActive = Path.Combine(blocked, LibraryDatabaseMigration.ActiveFile);
        using (var db = new SQLiteConnection(blockedActive)) db.Execute("CREATE TABLE Music(Id INTEGER)");
        File.WriteAllText(Path.Combine(blocked, "Backups"), "backup directory unavailable");
        await ExpectFailureAsync(blocked, check, "unwritable backup destination prevents the migration callback");
        using (var db = new SQLiteConnection(blockedActive))
        {
            check(db.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master") == 1 && db.ExecuteScalar<int>("PRAGMA user_version") == 0,
                "backup failure leaves schema and version intact");
            db.Execute("PRAGMA user_version=500");
        }
        await ExpectFailureAsync(blocked, check, "future global generation is rejected before any write", future: true);
        using (var db = new SQLiteConnection(blockedActive))
        {
            db.Execute("PRAGMA user_version=0");
            db.Execute("CREATE TABLE SongVersionSchema(Id INTEGER PRIMARY KEY,Version INTEGER)");
            db.Execute("INSERT INTO SongVersionSchema VALUES(1,500)");
        }
        await ExpectFailureAsync(blocked, check, "future song schema is rejected before any write", future: true);

        string fresh = Path.Combine(root, "fresh");
        var empty = await LibraryDatabaseMigration.OpenAsync(null, fresh, (db, _) => db.ExecuteAsync("CREATE TABLE Music(Id INTEGER)"),
            _ => throw new Exception("Unexpected first-install backup"));
        check(!Directory.Exists(Path.Combine(fresh, "Backups")) &&
            await empty.ExecuteScalarAsync<int>("PRAGMA user_version") == LibraryDatabaseMigration.CurrentVersion, "fresh install records current version without backup");
        await empty.CloseAsync();

        string interruptedInstall = Directory.CreateDirectory(Path.Combine(root, "empty-install")).FullName;
        File.WriteAllBytes(Path.Combine(interruptedInstall, LibraryDatabaseMigration.ActiveFile), []);
        var resumed = await LibraryDatabaseMigration.OpenAsync(null, interruptedInstall, (db, _) => db.ExecuteAsync("CREATE TABLE Music(Id INTEGER)"),
            _ => throw new Exception("Unexpected empty database backup"));
        check(await resumed.ExecuteScalarAsync<int>("PRAGMA user_version") == LibraryDatabaseMigration.CurrentVersion,
            "startup recovers a zero-length database left by an interrupted first install");
        await resumed.CloseAsync();
        await CheckCueUpgradeAsync(root, check);
    }

    private static async Task CheckCueUpgradeAsync(string root, Action<bool, string> check)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "cue-v4")).FullName;
        string active = Path.Combine(directory, LibraryDatabaseMigration.ActiveFile);
        using (var v4 = new SQLiteConnection(active))
        {
            v4.Execute("CREATE TABLE Music(Id INTEGER PRIMARY KEY,Path TEXT,SourceId INTEGER,SongId INTEGER)");
            v4.Execute("INSERT INTO Music VALUES(17,'X:/offline/local.flac',0,61),(18,'remote.flac',4,62)");
            v4.Execute("PRAGMA user_version=4");
        }
        string backup = "";
        try
        {
            await LibraryDatabaseMigration.OpenAsync(null, directory, async (db, _) =>
            {
                await CreateCueSchemaAsync(db);
                throw new InvalidOperationException("Interrupted CUE upgrade");
            }, path => backup = path);
            throw new Exception("Expected interrupted CUE upgrade");
        }
        catch (InvalidOperationException ex) when (ex.Message == "Interrupted CUE upgrade") { }
        string restoredPath = Path.Combine(directory, "independent-v4.db");
        File.Copy(backup, restoredPath);
        using (var restored = new SQLiteConnection(restoredPath, SQLiteOpenFlags.ReadOnly))
            check(restored.ExecuteScalar<int>("PRAGMA user_version") == 4 &&
                restored.ExecuteScalar<int>("SELECT COUNT(*) FROM Music WHERE Id IN (17,18)") == 2 &&
                restored.ExecuteScalar<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='CueSheet'") == 0,
                "the isolated v4 backup independently restores original local and remote identities");
        var migrated = await LibraryDatabaseMigration.OpenAsync(null, directory, async (db, upgrading) =>
        {
            check(upgrading && await db.ExecuteScalarAsync<int>("PRAGMA user_version") == 4, "interrupted CUE upgrade leaves the global version uncommitted");
            await CreateCueSchemaAsync(db);
        }, path => check(path == backup, "CUE migration reuses the original v4 backup"));
        var rows = await migrated.QueryAsync<Music>("SELECT * FROM Music ORDER BY Id");
        check(rows.Count == 2 && rows[0].Id == 17 && rows[0].SongId == 61 && rows[1].Id == 18 && rows[1].SourceId == 4 &&
            rows.All(row => !row.IsCueTrack && !row.IsCueContainer) &&
            await migrated.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM sqlite_master WHERE name='IX_Music_CurrentCueTrack' AND sql LIKE '%WHERE CueMappingActive=1%'") == 1,
            "v4 upgrades without mounting the source and preserves old rows with active-only track uniqueness");
        await migrated.CloseAsync();
        var reopened = await LibraryDatabaseMigration.OpenAsync(null, directory, async (db, upgrading) =>
        {
            check(!upgrading, "same-generation CUE schema reopens without a backup");
            await CreateCueSchemaAsync(db);
        }, _ => throw new Exception("Unexpected v5 backup"));
        await reopened.CloseAsync();

        static async Task CreateCueSchemaAsync(SQLiteAsyncConnection database)
        {
            await database.CreateTableAsync<Music>();
            await CueLibrarySchema.InitializeAsync(database);
            await CueMetadataOverrides.EnsureSchemaAsync(database);
        }
    }

    private static async Task ExpectFailureAsync(string directory, Action<bool, string> check, string message, bool future = false)
    {
        try
        {
            await LibraryDatabaseMigration.OpenAsync(null, directory, (_, _) => throw new Exception("Migration must not run"), _ => { });
        }
        catch (Exception ex) when (future ? ex is InvalidDataException : ex is IOException)
        {
            check(true, message);
            return;
        }
        throw new Exception(message);
    }
}
