using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

internal static class AutomaticLinkChecks
{
    internal static async Task RunAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "automatic-scans.db"));
        await database.InitializeAsync();
        try
        {
            await database.Connection.CreateTableAsync<PlayListMusic>();
            database.Versions = new(database.Connection);
            await database.Versions.InitializeAsync();
            await database.Lyrics.InitializeAsync();
            var first = new Music { Path = Path.Combine(root, "auto-first.flac"), Title = "Song", Author = "Artist", Album = "Album", Genre = "Jazz", AdditionalMetadataRead = true };
            var second = new Music { Path = Path.Combine(root, "auto-second.mp3"), Title = "SONG", Author = "Artist", Album = "Album", Genre = "Rock", AdditionalMetadataRead = true, IsFavorite = true };
            await database.AddExternalFileAsync(first, "[00:01.00]First");
            var imported = await database.AddExternalFileAsync(second, "[00:02.00]Second");
            Check(imported is not null && imported.SongId == first.SongId && imported.HasVersionLyrics && imported.IsFavorite,
                "external import returns already-linked, decorated version with retained embedded lyric conflict");
            var group = await database.Versions.GetVersionsAsync(first.Id);
            Check(group.Count == 2 && group[0].Genre == "Jazz" && group[1].Genre == "Rock", "real scan transaction retains version-specific tags");
            File.WriteAllText(first.Path, "present");
            File.WriteAllText(second.Path, "present");
            File.Delete(first.Path);
            await database.ReconcileExternalImportsAsync();
            group = await database.Versions.GetVersionsAsync(first.Id);
            Check(group.Single(m => m.Id == first.Id).IsMissing && SongVersionPolicy.Order(group)[0].Id == second.Id,
                "real directory reconciliation keeps the deleted row and chooses the surviving sibling");
            File.WriteAllText(first.Path, "restored");
            await database.ReconcileExternalImportsAsync();
            group = await database.Versions.GetVersionsAsync(first.Id);
            Check(group.All(m => !m.IsMissing) && SongVersionPolicy.Order(group)[0].Id == first.Id,
                "restoring the original path returns its version to automatic selection");
            var third = new Music { Path = Path.Combine(root, "auto-third.wav"), Title = "Other", Author = "Artist", Album = "Album" };
            await database.AddExternalFileAsync(third, "[00:03.00]Third");
            var document = await database.Lyrics.GetAsync(third.Id);
            await database.Connection.InsertAsync(new PlayListMusic { Id = 80, PlayListId = 1, MusicId = third.Id, SongId = third.SongId, RequestedMusicId = third.Id });
            third.Title = "Song";
            await database.SaveDetailsAsync(third, document.Document, document.Revision, default, lyricsChanged: false, expectedOwnerId: document.OwnerId);
            Check(third.SongId == first.SongId && (await database.Versions.GetVersionsAsync(first.Id)).Count == 3,
                "saving single-version identity tags immediately links and decorates the saved version");
            Check((await database.Lyrics.GetAsync(third.Id)).Document == document.Document, "metadata-triggered linking preserves the edited version's lyric ownership");
            var a = new Music { Path = Path.Combine(root, "batch-a.flac"), Title = "Batch", Author = "Artist", Album = "Album" };
            var b = new Music { Path = Path.Combine(root, "batch-b.mp3"), Title = "Batch", Author = "Artist", Album = "Album" };
            bool published = false;
            Func<IReadOnlyList<Music>, Task> callback = batch =>
            {
                Check(batch.Count == 2 && batch[0].SongId == batch[1].SongId && batch[0].SongId > 0,
                    "scan publication observes linked membership before the first UI batch callback");
                published = true;
                return Task.CompletedTask;
            };
            var method = typeof(MusicDatabaseService).GetMethod("CommitScanBatchAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            await (Task)method.Invoke(database, [new List<(Music Music, string Lyrics)> { (a, ""), (b, "") }, callback])!;
            Check(published, "scan callback completes after automatic linking");
            var snapshot = await database.Versions.ReadLibraryAsync(includePlaylists: true);
            Check(snapshot.Entries.Single().SongId == snapshot.Music.Single(m => m.Id == third.Id).SongId &&
                snapshot.Entries.Single().RequestedMusicId == third.Id, "one library snapshot resolves the playlist through the new song identity");
            var stale = await database.Connection.GetAsync<Music>(third.Id);
            long revision = 0;
            await database.Connection.RunInTransactionAsync(db =>
                revision = MusicMetadataStorage.UpdateAlbumFields(db, third.Id, "Bulk album", null, null, null));
            Check(revision == stale.MetadataRevision + 1, "album batch advances the exact file revision");
            try
            {
                await database.SaveDetailsAsync(stale, document.Document, document.Revision, default, lyricsChanged: false);
                throw new Exception("stale editor overwrote album batch");
            }
            catch (InvalidOperationException ex) when (ex.Message == "MusicMetadataConflict") { }
            Check((await database.Connection.GetAsync<Music>(third.Id)).Album == "Bulk album", "stale single-file editor cannot overwrite album edit");
            File.WriteAllText(third.Path, "present");
            var external = (await database.GetFolders()).Single(f => f.IsExternalImport);
            await database.RemoveFolder(external.Id);
            await database.ReconcileExternalImportsAsync();
            Check((await database.Versions.ReadLibraryAsync(false)).Music.Count == 0 && await database.FindMusicByPathAsync(third.Path) is null,
                "external removal stays hidden even while the file still exists");
            var restored = await database.AddExternalFileAsync(new Music { Path = third.Path, Title = third.Title, Album = third.Album, Author = third.Author }, "");
            Check(restored?.Id == third.Id && restored.SongId == first.SongId && (await database.Versions.ReadLibraryAsync(true)).Entries.Single().RequestedMusicId == restored.Id,
                "explicit reimport restores the same file, shared song and pinned relation");
            var folder = new Folder { Name = "Local root", Path = root, Type = Folder.TypeLocal };
            await database.Connection.InsertAsync(folder);
            await database.Connection.ExecuteAsync("UPDATE Music SET FolderPath=? WHERE Id=?", root, third.Id);
            await database.RemoveFolder(folder.Id);
            await database.ReconcileExternalImportsAsync();
            Check((await database.Connection.GetAsync<Music>(third.Id)).IsDetached && (await database.GetFoldersWithSongCountsAsync()).All(f => f.SongCount == 0),
                "removing a local root cannot feed its files back into the external library or folder counts");
        }
        finally { await database.Connection.CloseAsync(); }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }
}
