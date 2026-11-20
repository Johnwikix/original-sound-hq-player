using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class WatcherRegression
{
    internal static async Task RunAsync(string fixture)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture, "watched")).FullName;
        var database = new MusicDatabaseService(Path.Combine(fixture, "watcher.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlayListMusic>();
        database.Versions = new(database.Connection);
        await database.Versions.InitializeAsync();
        await database.Connection.InsertAsync(new Folder { Name = "watched", Path = root, Type = Folder.TypeLocal });
        var watcher = new LibraryWatcherService(database, new AppViewModel { IsFolderWatchEnabled = true },
            NullLogger<LibraryWatcherService>.Instance);
        await watcher.StartAsync();
        try
        {
            File.WriteAllText(Path.Combine(root, "cover.jpg"), "sidecar");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            await Task.Delay(1700);
            Check(database.WatchPublications == 0, "an unrelated file and empty directory do not publish");

            string path = Path.Combine(root, "new.mp3");
            File.WriteAllText(path, "audio");
            await WaitForPublicationAsync(1);
            var added = (await database.GetMusicListAsync()).Single();
            Check(added.Path == path, "audio creation adds a row");

            await AutoRescanService.AutoScan(database, new(StringComparer.OrdinalIgnoreCase) { [root] = [path] }, default);
            Check(database.WatchPublications == 1, "duplicate audio event does not publish");

            File.SetLastWriteTime(path, added.UpdateTime.AddMinutes(2));
            await WaitForPublicationAsync(2);
            var edited = (await database.GetMusicListAsync()).Single();
            Check(edited.Id == added.Id && edited.MetadataRevision == added.MetadataRevision + 1, "audio LastWrite updates the same row");

            string staging = Directory.CreateDirectory(Path.Combine(fixture, "staging")).FullName;
            File.WriteAllText(Path.Combine(staging, "inside.mp3"), "audio");
            string child = Path.Combine(root, "moved");
            Directory.Move(staging, child);
            await WaitForPublicationAsync(3);
            Check((await database.GetMusicListAsync()).Count == 2, "moving in a folder discovers its audio");

            string renamed = Path.Combine(root, "renamed");
            Directory.Move(child, renamed);
            await WaitForPublicationAsync(4);
            var renamedRows = await database.GetMusicListAsync();
            Check(renamedRows.Any(row => row.Path == Path.Combine(renamed, "inside.mp3") && !row.IsMissing) &&
                renamedRows.Any(row => row.Path == Path.Combine(child, "inside.mp3") && row.IsMissing),
                "rename reconciles both directory paths");

            await database.Connection.InsertAsync(new PlayListMusic { SongId = edited.SongId, MusicId = edited.Id, RequestedMusicId = edited.Id });
            File.Delete(path);
            await WaitForPublicationAsync(5);
            var deleted = (await database.GetMusicListAsync()).Single(row => row.Id == edited.Id);
            Check(deleted.IsMissing && !deleted.IsDetached &&
                (await database.Connection.Table<PlayListMusic>().FirstAsync()).RequestedMusicId == edited.Id,
                "audio deletion marks the version missing and keeps its fixed playlist entry");
            File.WriteAllText(path, "restored");
            await WaitForPublicationAsync(6);
            Check((await database.GetMusicListAsync()).Single(row => row.Id == edited.Id).IsMissing == false,
                "the original file identity is restored when the path reappears");
            Console.WriteLine("PASS: native folder events, no-op reconciliation, audio add/update/delete, directories moved in and renamed.");
        }
        finally { await watcher.StopAsync(); }

        async Task WaitForPublicationAsync(int expected)
        {
            var timeout = DateTime.UtcNow.AddSeconds(8);
            while (Volatile.Read(ref database.WatchPublications) < expected && DateTime.UtcNow < timeout)
                await Task.Delay(40);
            Check(database.WatchPublications == expected, $"expected {expected} publications, got {database.WatchPublications}");
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }
    }
}
