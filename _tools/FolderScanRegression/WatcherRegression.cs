using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class WatcherRegression
{
    private sealed class QueryPlan { public string Detail { get; set; } = ""; }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern bool MoveFileEx(string path, string destination, uint flags);

    internal static async Task RunAsync(string fixture)
    {
        string root = Directory.CreateDirectory(Path.Combine(fixture, "watched")).FullName;
        var database = new MusicDatabaseService(Path.Combine(fixture, "watcher.db"));
        await database.InitializeAsync();
        await database.Connection.CreateTableAsync<PlayListMusic>();
        database.Versions = new(database.Connection);
        await database.Versions.InitializeAsync();
        await database.Connection.InsertAsync(new Folder { Name = "watched", Path = root, Type = Folder.TypeLocal });
        var state = new AppViewModel { IsFolderWatchEnabled = true };
        var watcher = new LibraryWatcherService(database, state,
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

            await AutoRescanService.AutoScan(database, new(StringComparer.OrdinalIgnoreCase) { [root] = new() { Files = { path } } }, default);
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
            string upper = Directory.CreateDirectory(Path.Combine(root, "Édition")).FullName;
            string original = Path.Combine(upper, "track.mp3");
            File.WriteAllText(original, "music");
            await WaitForPublicationAsync(7);
            int originalId = (await database.GetMusicListAsync()).Single(row => row.Path == original).Id;
            string lower = Path.Combine(root, "édition");
            if (!MoveFileEx(upper, lower, 0)) throw new System.ComponentModel.Win32Exception();
            await WaitForPublicationAsync(8);
            var caseRows = (await database.GetMusicListAsync()).Where(row =>
                string.Equals(row.Path, original, StringComparison.OrdinalIgnoreCase)).ToArray();
            Check(caseRows.Length == 1 && caseRows[0].Id == originalId && caseRows[0].Path == Path.Combine(lower, "track.mp3"),
                "a case-only Unicode rename retains the original track and stores the new spelling");

            string album = Path.Combine(root, "Album🎵");
            string disc = Directory.CreateDirectory(Path.Combine(album, "CD1")).FullName;
            string nested = Path.Combine(disc, "track.mp3");
            File.WriteAllText(nested, "music");
            await WaitForPublicationAsync(9);
            Directory.Move(album, Path.Combine(fixture, "removed-album"));
            await WaitForPublicationAsync(10);
            Check((await database.GetMusicListAsync()).Single(row => row.Path == nested).IsMissing,
                "moving out a supplementary-character directory marks all descendants missing");
            string delayed = Path.Combine(root, "delayed.mp3");
            using (await LibraryOperationGate.EnterAsync())
            {
                using var observer = new FileSystemWatcher(root, "delayed.mp3") { NotifyFilter = NotifyFilters.FileName };
                var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                observer.Created += (_, _) => observed.TrySetResult();
                observer.EnableRaisingEvents = true;
                File.WriteAllText(delayed, "music");
                await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Task.Delay(1300); // Let the watcher debounce and queue behind the held maintenance lease.
                state.IsFolderWatchEnabled = false;
                state.IsFolderWatchEnabled = true;
            }
            await WaitForPublicationAsync(11);
            Check((await database.GetMusicListAsync()).Count(row => row.Path == delayed && !row.IsMissing) == 1,
                "a previously observed creation survives disabling and re-enabling the watcher without another write");
            Console.WriteLine("PASS: native no-op, add/update/delete, Unicode rename and subtree removal, interrupted change replay.");
        }
        finally { await watcher.StopAsync(); }

        var batch = new WatchedFolderChanges();
        for (int i = 0; i < 64; i++) batch.Files.Add(Path.Combine(root, $"absent-{i}.mp3"));
        var native = database.Connection.GetConnection();
        var lookups = new List<string>();
        native.Tracer = trace =>
        {
            int start = trace.IndexOf("SELECT Id,Path FROM Music", StringComparison.OrdinalIgnoreCase);
            if (start >= 0) lookups.Add(trace[start..].Split('\n')[0].TrimEnd());
        };
        native.Trace = true;
        try
        {
            await AutoRescanService.AutoScan(database, new() { [root] = batch }, default);
        }
        finally { native.Trace = false; native.Tracer = null; }
        Check(lookups.Count == 1 && database.WatchPublications == 11,
            "64 untracked file deletions use one lookup and publish nothing");
        var plan = await database.Connection.QueryAsync<QueryPlan>("EXPLAIN QUERY PLAN " + lookups[0], batch.Files.ToArray());
        Check(plan.Any(row => row.Detail.Contains("SEARCH Music USING INDEX IX_Music_Path_NoCase")),
            "the executed file deletion lookup uses the path index");
        Console.WriteLine("PASS: 64 file removals use one indexed lookup without a UI publication.");

        var registered = (await database.GetFolders()).Single();
        Task queued;
        using (await LibraryOperationGate.EnterAsync())
        {
            queued = database.RescanFolderByPath(root, isUpdate: false);
            Check(!queued.IsCompleted, "path scan waits for maintenance");
            await database.RemoveFolder(registered.Id);
        }
        await queued;
        Check(await database.Connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Music WHERE IsDetached=0") == 0,
            "a queued rescan cannot reattach songs after their root was removed");

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
