using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

internal static class CatalogDatabaseChecks
{
    internal static async Task RunAsync(CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("remote-catalog-migration-");
        try
        {
            string path = Path.Combine(directory.FullName, LibraryDatabaseMigration.ActiveFile);
            using (var old = new SQLiteConnection(path))
            {
                old.CreateTable<Music>();
                old.CreateTable<SongRecord>();
                old.Execute("CREATE TABLE WebDavSource (Id INTEGER PRIMARY KEY AUTOINCREMENT,Name TEXT,CredentialKey TEXT,BaseUri TEXT)");
                old.Execute("CREATE TABLE RemoteTrack (MusicId INTEGER PRIMARY KEY,SourceId INTEGER,Href TEXT)");
                old.Execute("INSERT INTO WebDavSource(Id,Name,CredentialKey,BaseUri) VALUES(37,'DAV','vault-37','https://example.test/dav/')");
                old.Execute("INSERT INTO Music(Id,SourceId,Path,Title,SongId,PlayCount,LyricsOffsetMs) VALUES(23,37,'webdav://37/dav/Track.flac','Track',29,5,100)");
                old.Execute("INSERT INTO RemoteTrack(MusicId,SourceId,Href) VALUES(23,37,'/dav/Track.flac')");
                old.Execute("PRAGMA user_version=5");
            }
            string backup = "";
            try
            {
                await LibraryDatabaseMigration.OpenAsync(null, directory.FullName, async (db, upgrading) =>
                {
                    Program.Check(upgrading, "v5 fixture not recognized");
                    await MusicDatabaseService.EnsureRemoteCatalogSchemaAsync(db, upgrading);
                    throw new InvalidOperationException("injected failure");
                }, file => backup = file);
                throw new CheckException("Expected migration interruption.");
            }
            catch (InvalidOperationException ex) when (ex.Message == "injected failure") { }
            using (var restored = new SQLiteConnection(backup, SQLiteOpenFlags.ReadOnly))
            {
                Program.Check(restored.ExecuteScalar<int>("PRAGMA user_version") == 5, "backup only created after modifying schema");
                Program.Check(restored.ExecuteScalar<string>("SELECT Path FROM Music WHERE Id=23") == "webdav://37/dav/Track.flac"
                    && restored.ExecuteScalar<string>("SELECT CredentialKey FROM WebDavSource WHERE Id=37") == "vault-37",
                    "standalone backup lost the original WebDAV and vault identities");
            }
            var database = await LibraryDatabaseMigration.OpenAsync(null, directory.FullName,
                MusicDatabaseService.EnsureRemoteCatalogSchemaAsync, file => Program.Check(file == backup, "retry replaced the original snapshot"));
            try
            {
                var legacy = await database.FindAsync<WebDavSource>(37);
                Program.Check(legacy is { Kind: RemoteSourceKind.WebDav, ConfigurationRevision: 0, CredentialKey: "vault-37" },
                    "v5 DAV row was not assigned the existing provider");
                var davTrack = await database.FindAsync<RemoteTrack>(23);
                var davMusic = await database.FindAsync<Music>(23);
                Program.Check(davTrack is { Href: "/dav/Track.flac", SourceId: 37 } &&
                    davMusic is { SourceId: 37, SongId: 29, PlayCount: 5, LyricsOffsetMs: 100 },
                    "migration rewrote a preserved DAV track");
                await VerifyIdentitiesAsync(database, token);
            }
            finally { await database.CloseAsync(); }
            var reopened = await LibraryDatabaseMigration.OpenAsync(null, directory.FullName, MusicDatabaseService.EnsureRemoteCatalogSchemaAsync,
                _ => throw new CheckException("A same-version reopen generated a backup."));
            await reopened.CloseAsync();
        }
        finally { directory.Delete(recursive: true); }
    }

    internal static async Task VerifyLiveSyncAsync(IRemoteMediaProvider provider, CatalogSession session, List<CatalogTrack> tracks,
        CancellationToken token)
    {
        var directory = Directory.CreateTempSubdirectory("remote-catalog-live-");
        try
        {
            var db = new SQLiteAsyncConnection(Path.Combine(directory.FullName, "catalog.db"));
            try
            {
                await db.CreateTableAsync<Music>();
                await db.CreateTableAsync<SongRecord>();
                await MusicDatabaseService.EnsureRemoteCatalogSchemaAsync(db, true);
                var source = await AddSourceAsync(db, provider.Kind);
                var store = new MusicDatabaseService(db);
                await SyncAsync(store, source, "first", tracks, token);
                var firstIds = await db.QueryScalarsAsync<int>("SELECT MusicId FROM RemoteTrack ORDER BY MusicId");
                List<CatalogTrack> repeated = [];
                await foreach (var track in provider.EnumerateAsync(session, token)) repeated.Add(track);
                await SyncAsync(store, source, "repeat", repeated, token);
                var secondIds = await db.QueryScalarsAsync<int>("SELECT MusicId FROM RemoteTrack ORDER BY MusicId");
                Program.Check(firstIds.Count == tracks.Count && firstIds.SequenceEqual(secondIds), "repeat live sync changed SQLite IDs");
                Program.Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RemoteTrack WHERE Missing=1") == 0,
                    "repeat live sync marked present tracks missing");
                Console.WriteLine($"PASS {provider.Kind} indexed and repeated {firstIds.Count} tracks using stable SQLite IDs");
            }
            finally { await db.CloseAsync(); }
        }
        finally { directory.Delete(recursive: true); }
    }

    private static async Task VerifyIdentitiesAsync(SQLiteAsyncConnection db, CancellationToken token)
    {
        var jellyfin = await AddSourceAsync(db, RemoteSourceKind.Jellyfin);
        var navidrome = await AddSourceAsync(db, RemoteSourceKind.Navidrome);
        Program.Check(jellyfin.Id > 37 && navidrome.Id > jellyfin.Id, "new provider IDs collided with WebDAV or each other");
        var store = new MusicDatabaseService(db);
        CatalogTrack item = new("a/b?", "Before", "Artist", "Same label", "a/1", "Artist", "", 1, 1, 2020, TimeSpan.FromSeconds(10),
            "flac", 4567, "cover-1", "new-cover-1");
        await SyncAsync(store, jellyfin, "first", [item], token);
        await SyncAsync(store, navidrome, "first", [item, item with { Id = "A/b?", AlbumId = "a/2" }], token);
        var rows = await db.QueryAsync<Music>("SELECT * FROM Music WHERE SourceId > 37 ORDER BY Id");
        Program.Check(rows.Count == 3 && rows.Select(row => row.Id).Distinct().Count() == 3, "IDs across providers or casing merged");
        var first = rows[0];
        await db.ExecuteAsync("UPDATE Music SET IsFavorite=1,PlayCount=42,LyricsOffsetMs=71 WHERE Id=?", first.Id);
        await SyncAsync(store, jellyfin, "second", [item with { Title = "After", AlbumId = "a/3" }], token);
        var updated = await db.FindAsync<Music>(first.Id);
        var resource = await db.FindAsync<RemoteTrack>(first.Id);
        Program.Check(updated.Id == first.Id && updated.SongId == first.SongId && updated.Title == "After" &&
            updated.IsFavorite && updated.PlayCount == 42 && updated.LyricsOffsetMs == 71 &&
            resource.ResourcePath == first.Path && resource.ResourcePath.Contains("a%2Fb%3F", StringComparison.Ordinal) && resource.ETag.Length == 0,
            "repeat sync overwrote stable row, statistics, or remote byte identity");

        await store.CommitCatalogTracksAsync(navidrome, "partial", [item], token);
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            try
            {
                await store.CompleteCatalogSyncAsync(navidrome, "partial", stopped.Token);
                throw new CheckException("Cancelled run finished reconciliation.");
            }
            catch (OperationCanceledException) { }
        }
        Program.Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RemoteTrack WHERE SourceId=? AND Missing=1", navidrome.Id) == 0,
            "cancelled partial scan hid missing rows");

        await db.ExecuteAsync("UPDATE WebDavSource SET ConfigurationRevision=1 WHERE Id=?", navidrome.Id);
        try
        {
            await store.CommitCatalogTracksAsync(navidrome, "obsolete", [item], token);
            throw new CheckException("Stale source config wrote to database.");
        }
        catch (WebDavException ex) when (ex.Code == "SourceUnavailable") { }
        navidrome.ConfigurationRevision = 1;
        await SyncAsync(store, navidrome, "complete", [item], token);
        Program.Check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM RemoteTrack WHERE SourceId=? AND Missing=1", navidrome.Id) == 1,
            "successful scan failed to mark a missing track");
    }

    private static async Task<WebDavSource> AddSourceAsync(SQLiteAsyncConnection db, RemoteSourceKind kind)
    {
        var source = new WebDavSource { Kind = kind, Name = kind.ToString(), BaseUri = "https://example.test/" };
        await db.InsertAsync(source);
        return source;
    }

    private static async Task SyncAsync(MusicDatabaseService store, WebDavSource source, string run, List<CatalogTrack> tracks,
        CancellationToken token)
    {
        foreach (var batch in tracks.Chunk(64)) await store.CommitCatalogTracksAsync(source, run, batch, token);
        await store.CompleteCatalogSyncAsync(source, run, token);
    }
}
