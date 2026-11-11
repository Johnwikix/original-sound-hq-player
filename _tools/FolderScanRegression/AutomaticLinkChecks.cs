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
            await database.Versions.InitializeAsync(database.Connection.DatabasePath);
            await database.Lyrics.InitializeAsync(database.Connection.DatabasePath);
            var first = new Music { Path = Path.Combine(root, "auto-first.flac"), Title = "Song", Author = "Artist", Album = "Album", Genre = "Jazz", AdditionalMetadataRead = true };
            var second = new Music { Path = Path.Combine(root, "auto-second.mp3"), Title = "SONG", Author = "Artist", Album = "Album", Genre = "Rock", AdditionalMetadataRead = true, IsFavorite = true };
            await database.AddExternalFileAsync(first, "[00:01.00]First");
            var imported = await database.AddExternalFileAsync(second, "[00:02.00]Second");
            Check(imported is not null && imported.SongId == first.SongId && imported.HasVersionLyrics && imported.IsFavorite,
                "external import returns already-linked, decorated version with retained embedded lyric conflict");
            var group = await database.Versions.GetVersionsAsync(first.Id);
            Check(group.Count == 2 && group[0].Genre == "Jazz" && group[1].Genre == "Rock", "real scan transaction retains version-specific tags");
            var third = new Music { Path = Path.Combine(root, "auto-third.wav"), Title = "Other", Author = "Artist", Album = "Album" };
            await database.AddExternalFileAsync(third, "[00:03.00]Third");
            var document = await database.Lyrics.GetAsync(third.Id);
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
        }
        finally { await database.Connection.CloseAsync(); }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException(name);
        Console.WriteLine("PASS: " + name);
    }
}
