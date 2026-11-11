using System.Diagnostics;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;

internal static class AutomaticChecks
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        var db = new SQLiteAsyncConnection(Path.Combine(root, "automatic.db"));
        try
        {
            await db.CreateTableAsync<Music>();
            await db.CreateTableAsync<PlayListMusic>();
            await db.CreateTableAsync<MusicLyrics>();
            var a = new Music { Title = " Song ", Author = "Artist", Album = "Album", Path = "a.flac", Genre = "Jazz", EncodingKind = 1 };
            var b = new Music { Title = "Ｓｏｎｇ", Author = "ARTIST", Album = "Album", Path = "b.mp3", Genre = "Rock", IsFavorite = true, Order = 12, LyricsOffsetMs = 71 };
            var c = new Music { Title = "Song", Author = "Artist", Album = "Other", Path = "c.mp3" };
            var live = new Music { Title = "Song (Live)", Author = "Artist", Album = "Album", Path = "live.mp3" };
            var remaster = new Music { Title = "Song (Remaster)", Author = "Artist", Album = "Album", Path = "remaster.mp3" };
            var unknown = new Music { Title = "Song", Author = "Unknown Artist", Album = "Album", Path = "unknown.mp3" };
            var unknown2 = new Music { Title = "Song", Author = "Unknown Artist", Album = "Album", Path = "unknown2.mp3" };
            await db.InsertAllAsync(new[] { a, b, c, live, remaster, unknown, unknown2 });
            await db.InsertOrReplaceAsync(new PlayListMusic { Id = 91, PlayListId = 1, MusicId = a.Id, Order = 9 });
            await db.InsertOrReplaceAsync(new PlayListMusic { Id = 92, PlayListId = 1, MusicId = b.Id, Order = 5, RequestedMusicId = b.Id });
            var store = new SongVersionStore(db);
            await store.InitializeAsync(db.DatabasePath);
            var lyrics = new LyricsRepository(db, new LyricsParser());
            await lyrics.InitializeAsync(db.DatabasePath);
            var original = await lyrics.GetAsync(a.Id);
            await lyrics.SaveAsync(a.Id, new(new("[00:01.00]A", LyricsFormat.Lrc), null), original.Revision, "User");
            original = await lyrics.GetAsync(b.Id);
            await lyrics.SaveAsync(b.Id, new(new("[00:02.00]B", LyricsFormat.Lrc), null), original.Revision, "User");
            await store.SetPreferredAsync(b.Id, b.Id);
            // 模拟用户已经用过 v1 版本功能的旧库；启动升级仍必须执行自动关联。
            await db.ExecuteAsync("UPDATE SongVersionSchema SET Version=1");
            await db.ExecuteAsync("DELETE FROM SongAutoLinkDirty");
            await store.InitializeAsync(db.DatabasePath);
            check(File.Exists(db.DatabasePath + ".autolink-v2.bak"), "already-upgraded libraries are backed up before automatic linking");
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 1, "v1 libraries automatically link normalized title/artist/album duplicates");
            var group = await store.GetVersionsAsync(a.Id);
            check(group.Count == 2 && group.All(m => m.IsFavorite && m.Order == 12), "automatic linking combines favorites and ignores optional info differences");
            check(group[0].Genre == "Jazz" && group[1].Genre == "Rock" && group[1].LyricsOffsetMs == 71, "automatic linking preserves per-version info and offsets");
            check((await store.GetSongAsync(a.Id))!.PreferredMusicId == b.Id, "automatic linking retains a manual preference from the source group");
            check(await db.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT SongId) FROM Music") == 6, "different albums, live/remaster and unknown tags remain separate");
            var entries = await db.Table<PlayListMusic>().OrderBy(e => e.Id).ToListAsync();
            check(entries.Count == 2 && entries[0].Id == 91 && entries[1].Id == 92 && entries[0].Order == 9 && entries[1].RequestedMusicId == b.Id && entries[0].SongId == entries[1].SongId,
                "automatic linking retains playlist entry identities, duplicate positions and exact-version intent");
            check((await lyrics.GetAsync(b.Id)).Document.Original.Content.Contains('B') && (await lyrics.GetSharedAsync(b.Id)).Document.Original.Content.Contains('A'),
                "automatic lyric conflicts retain the original timeline as an override");
            var auto = (await store.GetLastOperationAsync(group[0].SongId))!;
            check(auto.Kind == "AutoMerge", "one recoverable journal is stored for the entire duplicate bucket");
            await store.InitializeAsync(db.DatabasePath);
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 0, "restarting is idempotent and does not reprocess the library");
            await store.UndoAsync(auto.Id);
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 0 && (await store.GetVersionsAsync(a.Id)).Count == 1, "undoing automatic linking excludes restored groups from the next automatic pass");
            var preview = await store.PreviewMergeAsync(a.Id, b.Id, lyrics);
            await store.MergeAsync(preview, preview.TargetSongId, false);
            await store.SplitAsync(b.Id, lyrics);
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 0 && (await store.GetVersionsAsync(b.Id)).Count == 1, "manual splits remain separate across automatic passes");
            await db.ExecuteAsync("UPDATE SongVersionSchema SET Version=1");
            await db.ExecuteAsync("DELETE FROM SongAutoLink");
            await store.InitializeAsync(db.DatabasePath);
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 0 && (await store.GetVersionsAsync(b.Id)).Count == 1, "v1 split journals seed automatic exclusions during upgrade");

            var empty = new Music { Title = "New", Author = "Artist", Album = "Album", Path = "empty.flac" };
            var full = new Music { Title = "New", Author = "Artist", Album = "Album", Path = "full.mp3" };
            var cleared = new Music { Title = "New", Author = "Artist", Album = "Album", Path = "clear.mp3" };
            await AddAsync(empty);
            await AddAsync(full);
            await AddAsync(cleared);
            await db.InsertAsync(new MusicLyricsRecord { MusicId = full.Id, Lyrics = "[00:03.00]Embedded", LyricsFormat = LyricsFormat.Lrc, SourceKind = "Embedded" });
            await db.InsertAsync(new MusicLyricsRecord { MusicId = cleared.Id, SourceKind = "User" });
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 2, "new imports automatically link as a whole bucket");
            check((await lyrics.GetSharedAsync(empty.Id)).SourceKind == "User" && (await lyrics.GetSharedAsync(empty.Id)).Document.Original.Content.Length == 0,
                "an explicit user clear takes precedence over empty defaults and embedded lyrics");
            check((await lyrics.GetAsync(full.Id)).Document.Original.Content.Contains("Embedded"), "nonempty lyrics remain recoverable when the selected default is an explicit clear");
            check(await db.FindAsync<VersionLyricsOverrideRecord>(empty.Id) is null, "missing lyrics inherit the shared document without blank overrides");
            var d = new Music { Title = "Different", Author = "Artist", Album = "Album", Path = "d.mp3" };
            await AddAsync(d);
            await store.ApplyAutomaticLinksAsync(lyrics);
            await db.ExecuteAsync("UPDATE Song SET Title='New' WHERE Id=?", d.SongId);
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 1 && (await store.GetVersionsAsync(d.Id)).Count == 4, "changed canonical metadata queues only its affected automatic key");
            var late = new Music { Title = "Cancel", Author = "Artist", Album = "Album", Path = "late.mp3" };
            var late2 = new Music { Title = "Cancel", Author = "Artist", Album = "Album", Path = "late2.mp3" };
            await AddAsync(late);
            await AddAsync(late2);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await store.ApplyAutomaticLinksAsync(lyrics, canceled.Token); throw new Exception("Expected cancellation"); }
            catch (OperationCanceledException) { }
            await Task.WhenAll(store.ApplyAutomaticLinksAsync(lyrics), store.ApplyAutomaticLinksAsync(lyrics));
            check((await store.GetVersionsAsync(late.Id)).Count == 2, "cancellation retains pending work and concurrent callers serialize safely");
            var rollback = new Music { Title = "Rollback", Author = "Artist", Album = "Album", Path = "rollback.flac" };
            var rollback2 = new Music { Title = "Rollback", Author = "Artist", Album = "Album", Path = "rollback.mp3" };
            await AddAsync(rollback);
            await AddAsync(rollback2);
            int operations = await db.Table<SongVersionOperationRecord>().CountAsync();
            await db.ExecuteAsync("CREATE TRIGGER AutoLinkFailure BEFORE UPDATE OF SongId ON Music WHEN NEW.SongId<>OLD.SongId BEGIN SELECT RAISE(ABORT,'fixture failure'); END");
            try { await store.ApplyAutomaticLinksAsync(lyrics); throw new Exception("Expected SQLite failure"); }
            catch (SQLiteException) { }
            check((await db.FindAsync<Music>(rollback.Id)).SongId != (await db.FindAsync<Music>(rollback2.Id)).SongId &&
                await db.Table<SongVersionOperationRecord>().CountAsync() == operations, "failed automatic transaction rolls back membership and recovery journal together");
            await db.ExecuteAsync("DROP TRIGGER AutoLinkFailure");
            check(await store.ApplyAutomaticLinksAsync(lyrics) == 1, "failed automatic linking retains its pending key for retry");
            check(SongVersionPolicy.GetMatchKey("a:1", "b", "c") != SongVersionPolicy.GetMatchKey("a", "1:b", "c"), "length-prefixed automatic keys cannot collide through tag separators");
            var blank = new Music { Title = "Empty default", Author = "Artist", Album = "Album", Path = "blank.flac" };
            var nonempty = new Music { Title = blank.Title, Author = "Artist", Album = "Album", Path = "nonempty.mp3" };
            await AddAsync(blank);
            await AddAsync(nonempty);
            await db.InsertAsync(new MusicLyricsRecord { MusicId = nonempty.Id, Lyrics = "[00:01.00]Recovered", LyricsFormat = LyricsFormat.Lrc,
                SourceKind = "Embedded", SourceKey = "fixture", Diagnostic = "recovered" });
            await store.ApplyAutomaticLinksAsync(lyrics);
            var inherited = await lyrics.GetAsync(blank.Id);
            check(inherited.Document.Original.Content.Contains("Recovered") && inherited.SourceKey == "fixture" && inherited.Diagnostic == "recovered" &&
                await db.FindAsync<VersionLyricsOverrideRecord>(blank.Id) is null, "an empty default inherits nonempty lyrics with provenance and no blank override");
            var explicitClear = new Music { Title = blank.Title, Author = "Artist", Album = "Album", Path = "explicit-clear.mp3" };
            await AddAsync(explicitClear);
            await db.InsertAsync(new MusicLyricsRecord { MusicId = explicitClear.Id, SourceKind = "User" });
            await store.ApplyAutomaticLinksAsync(lyrics);
            var preservedClear = await lyrics.GetAsync(explicitClear.Id);
            check(preservedClear.OwnerId == -explicitClear.Id && preservedClear.SourceKind == "User" && preservedClear.Document.Original.Content.Length == 0 &&
                (await lyrics.GetSharedAsync(blank.Id)).Document.Original.Content.Contains("Recovered"), "a later explicit clear is preserved as an empty version override alongside the established shared document");
            check(await db.ExecuteScalarAsync<string>("PRAGMA quick_check") == "ok", "automatic operations retain SQLite integrity");

            async Task AddAsync(Music music) => await db.RunInTransactionAsync(connection =>
            {
                connection.Insert(music);
                SongVersionStore.AttachInTransaction(connection, music);
            });
        }
        finally { await db.CloseAsync(); }
        await ScaleAsync(root, check);
    }

    private static async Task ScaleAsync(string root, Action<bool, string> check)
    {
        var db = new SQLiteAsyncConnection(Path.Combine(root, "scale.db"));
        try
        {
            await db.CreateTableAsync<Music>();
            await db.CreateTableAsync<PlayListMusic>();
            await db.CreateTableAsync<MusicLyrics>();
            await db.RunInTransactionAsync(connection =>
            {
                for (int i = 0; i < 100_000; i++)
                    connection.Insert(new Music { Title = "Track " + (i < 2000 ? i / 2 : i), Author = "Artist", Album = "Album", Path = i + ".mp3" });
            });
            long allocated = GC.GetTotalAllocatedBytes();
            var watch = Stopwatch.StartNew();
            var store = new SongVersionStore(db);
            await store.InitializeAsync(db.DatabasePath);
            var lyrics = new LyricsRepository(db, new LyricsParser());
            await lyrics.InitializeAsync(db.DatabasePath);
            int count = await store.ApplyAutomaticLinksAsync(lyrics);
            watch.Stop();
            check(count == 1000 && await db.ExecuteScalarAsync<int>("SELECT COUNT(DISTINCT SongId) FROM Music") == 99_000, "100000-file library links 1000 duplicate pairs without quadratic whole-library comparisons");
            Console.WriteLine($"SCALE cold migration+link: {watch.ElapsedMilliseconds} ms, process allocation {GC.GetTotalAllocatedBytes() - allocated:N0} bytes");
            watch.Restart();
            await store.ApplyAutomaticLinksAsync(lyrics);
            watch.Stop();
            check(await db.Table<SongAutoLinkDirtyRecord>().CountAsync() == 0 && await db.Table<SongAutoLinkPendingRecord>().CountAsync() == 0, "large-library dirty and pending queues drain completely");
            Console.WriteLine($"SCALE unchanged automatic pass: {watch.ElapsedMilliseconds} ms");
        }
        finally { await db.CloseAsync(); }
    }
}
