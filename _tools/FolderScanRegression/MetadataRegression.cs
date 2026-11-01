using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

internal static class MetadataRegression
{
    public static async Task RunAsync(string root)
    {
        using var services = new ServiceCollection().AddSingleton<NotificationService>().BuildServiceProvider();
        App.Services = services;
        string dbPath = Path.Combine(root, "metadata.db");
        string filePath = Path.Combine(root, "metadata.mp3");
        await File.WriteAllTextAsync(filePath, "original");
        var db = new MusicDatabaseService(dbPath);
        await db.InitializeAsync();
        var music = new Music { Path = filePath, Title = "first", AlbumArtist = "album artist", Genre = "Jazz", Composer = "composer", Lyricist = "lyricist", Conductor = "conductor", CatalogNumber = "CAT-1", AdditionalMetadataRead = true };
        await db.Connection.InsertAsync(music);
        var cover = new byte[] { 1, 2, 3 };
        var parser = new WinUIMusicPlayer.Services.Lyrics.LyricsParser();
        var revision = (await db.Lyrics.GetAsync(music.Id)).Revision;
        await db.QueueMetadataWriteAsync(music, cover, parser.Import("[00:01.00]lyrics", "[00:01.00]translation"), revision++);
        var initialRequest = await db.Connection.FindAsync<PendingMetadataWrite>(filePath);
        Check(initialRequest.MetadataVersion == 1 && initialRequest.AlbumArtist == "album artist" && initialRequest.CatalogNumber == "CAT-1", "additional tag snapshot persisted");
        Check((await db.Connection.FindAsync<Music>(music.Id)).Genre == "Jazz", "additional tags stored with editor transaction");
        cover[0] = 9;
        Check((await db.Connection.FindAsync<PendingMetadataWrite>(filePath)).Cover![0] == 1, "cover snapshot");
        using (var held = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await db.FlushMetadataWritesAsync(default);
            Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 1, "locked write retained");
            Check(NotificationService.Count == 0, "sharing violation is quiet");
            music.Title = "latest";
            await db.QueueMetadataWriteAsync(music, null, parser.Import("[00:01.00]new lyrics"), revision++);
            var batch = new List<(Music Music, string Lyrics)> { (new Music { Id = music.Id, Path = filePath, Title = "stale" }, "old") };
            var commit = typeof(MusicDatabaseService).GetMethod("CommitScanBatchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)commit.Invoke(db, [batch, null])!;
            Check((await db.Connection.FindAsync<Music>(music.Id)).Title == "latest", "scan preserves pending edits");
        }
        await db.Connection.CloseAsync();
        db = new MusicDatabaseService(dbPath);
        // Reopen the persisted queue without recreating test indexes.
        await db.FlushMetadataWritesAsync(default);
        Check(ToolUtils.LastWrittenMusic is { AdditionalMetadataRead: true, Genre: "Jazz", CatalogNumber: "CAT-1" }, "restart restores all queued additional fields");
        Check(await File.ReadAllTextAsync(filePath) == "latest", "restart applies latest snapshot");
        Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 0, "success removes request");
        await db.QueueMetadataWriteAsync(music, null, LyricsDocument.Empty, revision++);
        var writer = ToolUtils.WriteMetadata;
        ToolUtils.WriteMetadata = (_, _) => throw new IOException("unsupported tag write");
        try
        {
            await db.FlushMetadataWritesAsync(default);
            await db.FlushMetadataWritesAsync(default);
            Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 1, "failure retained");
            Check(NotificationService.Count == 1, "failure reported once");
        }
        finally { ToolUtils.WriteMetadata = writer; }
        await db.FlushMetadataWritesAsync(default);
        Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 0, "failed write can recover");
        // An upgraded old queue row has no value in the newly added preservation column.
        await db.Connection.ExecuteAsync("INSERT INTO PendingMetadataWrite(Path,Title,Lyrics,Krc,LyricsSchemaVersion) VALUES(?,?,?,?,0)",
            filePath, "legacy queue", "[00:01.00]line candidate", "[1000,1000]word(1000,1000)");
        using (var held = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            await db.FlushMetadataWritesAsync(default);
            var migrated = await db.Connection.FindAsync<PendingMetadataWrite>(filePath);
            Check(migrated.LyricsSchemaVersion == 2 && migrated.LyricsFormat == LyricsFormat.Qrc &&
                migrated.LegacyLyrics == "[00:01.00]line candidate" && migrated.Krc == "[1000,1000]word(1000,1000)", "legacy queue keeps both source snapshots while locked");
        }
        await db.FlushMetadataWritesAsync(default);
        Check(ToolUtils.LastWrittenMusic is { AdditionalMetadataRead: false }, "legacy queue must preserve unrecorded optional file tags");
        await db.Connection.InsertAsync(new PendingMetadataWrite { Path = filePath, Title = "invalid queue", Lyrics = "untimed unknown" });
        await db.FlushMetadataWritesAsync(default);
        Check(await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 1 && await File.ReadAllTextAsync(filePath) == "legacy queue", "unconvertible legacy lyrics cannot silently erase audio tags");
        await db.Connection.DeleteAllAsync<PendingMetadataWrite>();
        var unknownMusic = new Music { Path = Path.Combine(root, "unknown-metadata.mp3"), Title = "old title" };
        await File.WriteAllTextAsync(unknownMusic.Path, "existing file lyrics");
        await db.Connection.InsertAsync(unknownMusic);
        await db.Connection.InsertAsync(new MusicLyrics { MusicId = unknownMusic.Id, Lyrics = "Untimed legacy lyrics" });
        var unknown = await db.Lyrics.GetAsync(unknownMusic.Id);
        unknownMusic.Title = "metadata-only title";
        await db.SaveDetailsAsync(unknownMusic, unknown.Document, unknown.Revision, default, lyricsChanged: false);
        var savedUnknown = await db.Lyrics.GetAsync(unknownMusic.Id);
        Check(savedUnknown.Document == unknown.Document && savedUnknown.SourceKind == unknown.SourceKind && savedUnknown.Diagnostic == unknown.Diagnostic &&
            (await db.Connection.FindAsync<Music>(unknownMusic.Id)).Title == "metadata-only title", "real metadata transaction retains unknown lyrics and diagnostics");
        try
        {
            await db.SaveDetailsAsync(unknownMusic, unknown.Document, unknown.Revision, default, lyricsChanged: false);
            throw new Exception("metadata-only edit ignored revision conflict");
        }
        catch (InvalidOperationException ex) when (ex.Message == "LyricsEditConflict") { }
        await db.QueueMetadataWriteAsync(unknownMusic, null, savedUnknown.Document, savedUnknown.Revision, lyricsChanged: false);
        Check((await db.Connection.FindAsync<PendingMetadataWrite>(unknownMusic.Path)).PreserveFileLyrics, "unknown unchanged lyrics queue a metadata-only tag write");
        await db.Connection.CloseAsync();
        db = new MusicDatabaseService(dbPath);
        await db.FlushMetadataWritesAsync(default);
        Check(ToolUtils.LastPreserveLyrics && await db.Connection.Table<PendingMetadataWrite>().CountAsync() == 0 &&
            await File.ReadAllTextAsync(unknownMusic.Path) == "metadata-only title", "restart applies metadata without attempting invalid lyrics conversion");
        savedUnknown = await db.Lyrics.GetAsync(unknownMusic.Id);
        await db.QueueMetadataWriteAsync(unknownMusic, null, LyricsDocument.Empty, savedUnknown.Revision);
        await db.FlushMetadataWritesAsync(default);
        Check(!ToolUtils.LastPreserveLyrics, "explicit clear still writes an empty lyric tag");
        var queuedTags = new Music { Path = Path.Combine(root, "queued-tags.mp3"), Title = "first tag edit", Genre = "Jazz", AlbumArtist = "Album artist", AdditionalMetadataRead = true, PlayCount = 8 };
        await File.WriteAllTextAsync(queuedTags.Path, "original");
        await db.Connection.InsertAsync(queuedTags);
        var queuedRevision = (await db.Lyrics.GetAsync(queuedTags.Id)).Revision;
        await db.QueueMetadataWriteAsync(queuedTags, null, LyricsDocument.Empty, queuedRevision);
        var unreadEditor = new Music { Id = queuedTags.Id, Path = queuedTags.Path, Title = "later core edit" };
        queuedRevision = (await db.Lyrics.GetAsync(queuedTags.Id)).Revision;
        await db.QueueMetadataWriteAsync(unreadEditor, null, LyricsDocument.Empty, queuedRevision);
        var retainedTags = await db.Connection.FindAsync<PendingMetadataWrite>(queuedTags.Path);
        Check(retainedTags.MetadataVersion == 1 && retainedTags.Genre == "Jazz", "unread window cannot replace queued optional tags with empty values");
        await db.FlushMetadataWritesAsync(default);
        Check(ToolUtils.LastWrittenMusic is { Genre: "Jazz", AdditionalMetadataRead: true } &&
            (await db.Connection.FindAsync<Music>(queuedTags.Id)).PlayCount == 8, "replacement queue preserves tags and playback-owned columns");
        using var oneShot = new OneShotPlaybackService(new(), new(), db, new(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OneShotPlaybackService>.Instance);
        var imported = new Music { Path = Path.Combine(root, "cleared-before-import.mp3") };
        await db.Connection.InsertAsync(imported);
        OneShotLyricsCache.Entries[imported.Path] = new(LyricsDocument.Empty, "User");
        var migrate = typeof(OneShotPlaybackService).GetMethod("MigrateOneShotLyricsAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        await (Task)migrate.Invoke(oneShot, [imported, null])!;
        Check((await db.Lyrics.GetAsync(imported.Id)) is { SourceKind: "User", Document.Original.Content: "" }, "external clear remains authoritative after import into library");
        await db.Connection.CloseAsync();
        Console.WriteLine("PASS: metadata lock/release, snapshot, replacement, restart, scan protection, failure recovery, unknown-lyrics metadata save, persisted tag preservation and external clear import.");
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
    }
}
