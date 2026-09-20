using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Windows.Storage.Pickers;
using WinUIMusicPlayer;
using WinUIMusicPlayer.AudioConverters;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using WinUIMusicPlayer.Utils;
using Windows.Storage;

internal static class RegressionSuite
{
    private sealed class ScanProgress : IProgress<int>
    {
        public List<int> Values { get; } = [];
        public void Report(int value) => Values.Add(value);
    }

    // 模拟拖放返回的接口包装对象，不假定它的 CLR 类型是 StorageFolder。
    private sealed class DroppedFolderItem(string path) : IStorageItem
    {
        public string Path => path;
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("FAIL: " + message);
    }

    public static async Task RunAsync(string root)
    {
        await PipelineAsync();
        await DatabaseAsync(root);
        await ExternalImportAsync(root);
        await ExternalImportPublicationAsync(root);
        await StartupCancellationAsync(root);
        PlaybackSnapshot();
        await BenchmarkAsync();
        ProbeAudio(root);
        Console.WriteLine("PASS: all folder-scan regression checks.");
    }

    private static async Task PipelineAsync()
    {
        int read = 0, firstCount = 0;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = ScanPipeline.RunAsync(Enumerable.Range(0, 100_000), (value, token) =>
        {
            Interlocked.Increment(ref read);
            return ValueTask.FromResult(value);
        }, async batch =>
        {
            firstCount = batch.Count;
            entered.TrySetResult();
            await release.Task;
            throw new InvalidOperationException("sink failed");
        }, TimeSpan.Zero);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(100);
        Check(read <= firstCount + ScanPipeline.BatchSize + ScanPipeline.WorkerCount, "unbounded producer backlog");
        release.SetResult();
        try { await scan.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("missing failure"); }
        catch (InvalidOperationException ex) when (ex.Message == "sink failed") { }
        int finalRead = read;
        await Task.Delay(50);
        Check(read == finalRead, "workers survived consumer failure");

        using var cancellation = new CancellationTokenSource();
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = ScanPipeline.RunAsync(Enumerable.Range(0, 100), async (value, token) =>
        {
            running.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return value;
        }, _ => Task.CompletedTask, TimeSpan.Zero, cancellation.Token);
        await running.Task;
        cancellation.Cancel();
        try { await cancelled.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("missing cancellation"); }
        catch (OperationCanceledException) { }

        var times = new List<long>();
        int count = 0;
        await ScanPipeline.RunAsync(Enumerable.Range(0, 400), (value, _) => ValueTask.FromResult(value), batch =>
        {
            Check(batch.Count <= 128, "oversized batch");
            count += batch.Count;
            times.Add(Stopwatch.GetTimestamp());
            return Task.CompletedTask;
        }, TimeSpan.FromMilliseconds(500));
        Check(count == 400, "final batch not drained");
        for (int i = 1; i < times.Count; i++)
            Check(Stopwatch.GetElapsedTime(times[i - 1], times[i]).TotalMilliseconds >= 480, "cadence exceeded 2 Hz");
        var owner = LibraryOperationGate.TryEnter();
        Check(owner is not null && LibraryOperationGate.TryEnter() is null, "overlapping operation admitted");
        var queued = LibraryOperationGate.EnterAsync();
        Check(!queued.IsCompleted, "background scan bypassed manual operation");
        owner!.Dispose();
        using (await queued.WaitAsync(TimeSpan.FromSeconds(3)))
            Check(LibraryOperationGate.IsBusy, "queued scan did not acquire gate");
        Console.WriteLine($"PASS: bounded backlog ({finalRead}), failure/cancellation cleanup, 128-item batches and 500 ms cadence.");
    }

    private static async Task DatabaseAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "test.db"));
        await database.InitializeAsync();
        try
        {
            string musicRoot = Path.Combine(root, "Music");
            Directory.CreateDirectory(musicRoot);
            var oldFolder = new Folder { Path = musicRoot, Name = "Music", Type = "local", SongCount = 0 };
            await database.Connection.InsertAsync(oldFolder);
            string oldPath = Path.Combine(musicRoot, "existing.mp3");
            File.WriteAllText(oldPath, "");
            var oldSong = new Music { Path = oldPath, FolderPath = musicRoot, UpdateTime = File.GetLastWriteTime(oldPath) };
            await database.Connection.InsertAsync(oldSong);
            var appVm = new AppViewModel(); // Empty until startup loads SongsSource.
            var vm = new AddFolderViewModel(database, appVm, NullLogger<AddFolderViewModel>.Instance,
                new FolderAccessService(NullLogger<FolderAccessService>.Instance));
            vm.Activate();
            App.Services = new ServiceCollection().AddSingleton(database).AddSingleton(appVm)
                .AddSingleton(vm).AddSingleton<MainPage>().BuildServiceProvider();
            for (int i = 0; vm.FolderList.Count == 0 && i < 100; i++) await Task.Delay(10);
            Check(appVm.SongsSource.Count == 0 && vm.FolderList.Single().SongCount == 1,
                "old database count depends on unloaded SongsSource");
            await appVm.RefreshSongsSourceAsync();

            File.WriteAllText(Path.Combine(musicRoot, "first.mp3"), "");
            File.WriteAllText(Path.Combine(musicRoot, "slow.mp3"), "");
            ToolUtils.SlowFile = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            appVm.BatchApplied += _ => first.TrySetResult();
            var scan = vm.RescanFolderWithLoadingAsync(oldFolder.Id);
            await first.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Check(vm.IsScanning, "busy state missing");
            Check((await database.GetMusicListAsync()).Count == 2, "publication preceded commit");
            Check(appVm.SongsSource.Count == 2 && vm.FolderList.Single().SongCount == 2, "progressive VM count missing");
            Check(!vm.AddFolderCommand.CanExecute(null), "add command enabled");
            Check(!FolderCommands.RescanFolderCommand.CanExecute(oldFolder), "rescan command enabled");
            Check(!FolderCommands.RemoveFolderCommand.CanExecute(oldFolder), "remove command enabled");
            int reads = ToolUtils.Reads, confirmations = DialogHelper.Calls;
            await vm.RescanFolderWithLoadingAsync(oldFolder.Id);
            await vm.RemoveFolderWithLoadingAsync(oldFolder.Id);
            await vm.DropFoldersAsync([new StorageFolder(Path.Combine(root, "ignored"))]);
            Check(reads == ToolUtils.Reads && confirmations == DialogHelper.Calls, "direct call bypassed guard");
            ToolUtils.SlowFile.SetResult();
            await scan;
            Check(!vm.IsScanning && appVm.SongsSource.Count == 3, "final reconciliation/release failed");
            Check(FolderCommands.RemoveFolderCommand.CanExecute(oldFolder), "command did not recover");

            var picker = new TaskCompletionSource<PickFolderResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
            FolderPicker.Pick = () => picker.Task;
            var picking = vm.AddFolderWithLoadingAsync();
            Check(vm.IsScanning, "picker does not reserve operation");
            await vm.RemoveFolderWithLoadingAsync(oldFolder.Id);
            Check(DialogHelper.Calls == confirmations, "remove entered while picker open");
            picker.SetResult(null);
            await picking;
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            DialogHelper.Confirm = () => confirmation.Task;
            var removing = vm.RemoveFolderWithLoadingAsync(oldFolder.Id);
            Check(vm.IsScanning, "confirmation does not reserve operation");
            await vm.RescanFolderWithLoadingAsync(oldFolder.Id);
            confirmation.SetResult(false);
            await removing;

            string sibling = musicRoot + "2";
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "other.mp3"), "");
            await vm.DropFoldersAsync([new StorageFolder(sibling)]);
            Check((await database.GetFolders()).Count == 2, "Music2 treated as descendant of Music");
            await vm.DropFoldersAsync([new StorageFolder(musicRoot)]);
            Check((await database.GetMusicListAsync()).Count == 4, "duplicate scan inserted rows");

            oldSong.Title = "old title"; oldSong.IsFavorite = true; oldSong.PlayCount = 17;
            oldSong.LyricsOffsetMs = 250; oldSong.Order = 9;
            await database.Connection.UpdateAsync(oldSong);
            await database.Connection.InsertOrReplaceAsync(new MusicLyrics { MusicId = oldSong.Id, Lyrics = "user lyrics" });
            await vm.RescanFolderWithLoadingAsync(oldFolder.Id);
            var updated = await database.Connection.FindAsync<Music>(oldSong.Id);
            Check(updated.Title == "existing.mp3", "metadata not persisted");
            Check(updated.IsFavorite && updated.PlayCount == 17 && updated.LyricsOffsetMs == 250 && updated.Order == 9, "user state overwritten");
            Check((await database.Connection.FindAsync<MusicLyrics>(oldSong.Id)).Lyrics == "user lyrics", "user lyrics overwritten");
            reads = ToolUtils.Reads;
            await database.ScanChangedFolderAsync(musicRoot);
            Check(reads == ToolUtils.Reads, "startup reread unchanged files");
            File.SetLastWriteTime(oldPath, DateTime.Now.AddMinutes(1));
            await database.ScanChangedFolderAsync(musicRoot);
            Check(reads + 1 == ToolUtils.Reads, "startup missed changed file");

            var offline = new Music { Path = Path.Combine(root, "offline", "lost.mp3"), FolderPath = Path.Combine(root, "offline") };
            await database.Connection.InsertAsync(offline);
            try { await database.ScanChangedFolderAsync(offline.FolderPath); throw new Exception("missing enumeration error"); }
            catch (DirectoryNotFoundException) { }
            Check(await database.Connection.FindAsync<Music>(offline.Id) is not null, "offline root caused deletion");
            File.Delete(oldPath);
            Check(await database.RescanFolderWithOutUpdateAll(musicRoot) == 1, "deletion-only scan not reported");
            Check(await database.Connection.FindAsync<MusicLyrics>(oldSong.Id) is null, "orphaned lyrics");
            DialogHelper.Confirm = () => Task.FromResult(true);
            await vm.RemoveFolderWithLoadingAsync(oldFolder.Id);
            Check((await database.GetMusicListAsync()).Any(m => m.FolderPath == sibling), "remove crossed directory boundary");
            string rejected = Path.Combine(root, "reject.mp3");
            File.WriteAllText(rejected, "");
            await database.Connection.ExecuteAsync(
                "CREATE TRIGGER RejectLyrics BEFORE INSERT ON MusicLyrics " +
                "WHEN (SELECT Title FROM Music WHERE Id=NEW.MusicId) = 'reject.mp3' " +
                "BEGIN SELECT RAISE(ABORT, 'test rollback'); END");
            try
            {
                try { await database.AddMusicList([new Music { Path = rejected }]); throw new Exception("missing transaction failure"); }
                catch (SQLite.SQLiteException) { }
                Check(!(await database.GetMusicListAsync()).Any(m => m.Path == rejected), "song survived failed lyrics transaction");
            }
            finally { await database.Connection.ExecuteAsync("DROP TRIGGER RejectLyrics"); }
            string droppedRoot = Path.Combine(root, "DroppedInterfaceFolder");
            Directory.CreateDirectory(droppedRoot);
            string droppedSong = Path.Combine(droppedRoot, "dropped.mp3");
            File.WriteAllText(droppedSong, "");
            await vm.DropFoldersAsync([new DroppedFolderItem(droppedRoot)]).WaitAsync(TimeSpan.FromSeconds(5));
            Check((await database.GetFolders()).Any(f => f.Path == droppedRoot),
                "interface-only dropped folder was silently skipped before scanning");
            Check(appVm.SongsSource.Any(m => m.Path == droppedSong), "dropped folder did not scan/publish songs");
            Check(!vm.IsScanning, "drop did not release operation gate");
            Console.WriteLine("PASS: interface-only dropped folder is inserted, scanned and published.");
            Console.WriteLine("PASS: real SQLite + VM/commands: old DB counts, progressive commits, all guards, picker/dialog races, dedup, user state, startup diff and safe deletion.");
        }
        finally { await database.Connection.CloseAsync(); }
    }

    // 外部打开入库的库内语义：归属按路径现算、扫描根加入/移除的处理、存在性对账与虚拟行移除。
    // 归属与对账使用真实外部导入写路径。
    private static async Task ExternalImportAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "external.db"));
        await database.InitializeAsync();
        try
        {
            string dir = Path.Combine(root, "ExternalMusic");
            Directory.CreateDirectory(dir);
            string importedPath = Path.Combine(dir, "imported.mp3");
            File.WriteAllText(importedPath, "");
            var imported = new Music { Path = importedPath, FolderPath = dir, Title = "imported.mp3" };
            Check(await database.AddExternalFileAsync(imported, "") is not null, "external import failed");

            var counted = await database.GetFoldersWithSongCountsAsync();
            Check(counted.Single(f => f.IsExternalImport).SongCount == 1, "external import count missing");

            // 之后把包含导入文件的目录加入扫描：不重复入库，归属移交给扫描根。
            await database.CheckFolderBeforeAdd(new StorageFolder(dir));
            Check((await database.GetMusicListAsync()).Count(m => m.Path == importedPath) == 1,
                "adding folder duplicated imported row");
            counted = await database.GetFoldersWithSongCountsAsync();
            Check(counted.Single(f => f.IsExternalImport).SongCount == 0, "ownership did not transfer to scan root");
            Check(counted.Single(f => f.Path == dir).SongCount == 1, "scan root count missing");

            // 对账：盘根可达且文件存在 → 保留；文件确认缺失 → 行与歌词一并删除。
            string strayDir = Path.Combine(root, "ExternalStray");
            Directory.CreateDirectory(strayDir);
            string strayPath = Path.Combine(strayDir, "stray.mp3");
            File.WriteAllText(strayPath, "");
            var stray = new Music { Path = strayPath, FolderPath = strayDir, Title = "stray.mp3" };
            await database.Connection.InsertAsync(stray);
            await database.Connection.InsertOrReplaceAsync(new MusicLyrics { MusicId = stray.Id, Lyrics = "x" });
            Check(!await database.ReconcileExternalImportsAsync(), "present import reported deletion");
            File.Delete(strayPath);
            Check(await database.ReconcileExternalImportsAsync(), "missing import kept");
            Check(await database.Connection.FindAsync<Music>(stray.Id) is null, "missing import row survived");
            Check(await database.Connection.FindAsync<MusicLyrics>(stray.Id) is null, "missing import lyrics survived");

            // 移除扫描根：其中的（原导入）行随路径删除，与库内歌曲一致。
            var scanFolder = (await database.GetFolders()).Single(f => f.Path == dir);
            await database.RemoveFolder(scanFolder.Id);
            Check(!(await database.GetMusicListAsync()).Any(m => m.Path == importedPath),
                "imported row survived scan-root removal");

            // 移除虚拟行：全部导入行与虚拟行本身一并删除。
            string stray2Path = Path.Combine(strayDir, "stray2.mp3");
            File.WriteAllText(stray2Path, "");
            await database.Connection.InsertAsync(new Music { Path = stray2Path, FolderPath = strayDir });
            var external = (await database.GetFolders()).Single(f => f.IsExternalImport);
            await database.RemoveFolder(external.Id);
            Check(!(await database.GetMusicListAsync()).Any(m => m.Path == stray2Path),
                "virtual removal left imported rows");
            Check(!(await database.GetFolders()).Any(f => f.IsExternalImport), "virtual row survived removal");
            Console.WriteLine("PASS: external imports: path-based ownership, folder add/remove semantics, reconcile and virtual removal.");
        }
        finally { await database.Connection.CloseAsync(); }
    }

    private static async Task ExternalImportPublicationAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "external-publication.db"));
        await database.InitializeAsync();
        var app = new AppViewModel();
        var folders = new AddFolderViewModel(database, app, NullLogger<AddFolderViewModel>.Instance,
            new FolderAccessService(NullLogger<FolderAccessService>.Instance));
        using var services = new ServiceCollection().AddSingleton(database).AddSingleton(app).BuildServiceProvider();
        App.Services = services;
        using var oneShot = new OneShotPlaybackService(app, new AppLifecycle(), database,
            new NotificationService(), NullLogger<OneShotPlaybackService>.Instance);
        try
        {
            // Wait for the empty page load before import, reproducing an already-visible management page.
            folders.Activate();
            await (Task)typeof(AddFolderViewModel).GetField("_initialLoad",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(folders)!;
            string path = Path.Combine(root, "reopen.mp3");
            File.WriteAllText(path, "");
            oneShot.PlayNow(path);
            var first = await ResolvedAsync(oneShot);
            Check(first is { Id: > 0 }, "first open was not imported");
            await folders.PublishImportedAsync(first!);
            Check(folders.FolderList.Count == 1 && folders.FolderList[0].SongCount == 1,
                "first import did not publish virtual folder/count");
            Check(folders.EmptyVisibility == Microsoft.UI.Xaml.Visibility.Collapsed
                && folders.ListVisibility == Microsoft.UI.Xaml.Visibility.Visible, "import left empty page visible");
            await folders.PublishImportedAsync(first!);
            Check(app.SongsSource.Count == 1 && folders.FolderList[0].SongCount == 1
                && app.SourceNotifications == 1, "repeated publication duplicated song/count/notification");

            await database.RemoveFolder(folders.FolderList[0].Id);
            await app.RefreshSongsSourceAsync();
            oneShot.PlayNow(path);
            var second = await ResolvedAsync(oneShot);
            Check(second is { Id: > 0 } && second.Id != first!.Id
                && (await database.FindMusicByPathAsync(path))?.Id == second.Id, "reopen reused removed database identity");
            await folders.PublishImportedAsync(second!);
            Check(app.SongsSource.Count == 1 && app.SongsSource[0].Id == second!.Id
                && folders.FolderList.Single().SongCount == 1, "reimport publication was stale");

            // A failed resolution must also be retried for the same path.
            await database.RemoveFolder(folders.FolderList.Single().Id);
            string retryPath = Path.Combine(root, "broken-retry.mp3");
            File.WriteAllText(retryPath, "");
            oneShot.PlayNow(retryPath);
            Check(await ResolvedAsync(oneShot) is null, "broken metadata unexpectedly resolved");
            var repaired = new Music { Path = retryPath, FolderPath = root };
            await database.AddExternalFileAsync(repaired, "");
            oneShot.PlayNow(retryPath);
            Check((await ResolvedAsync(oneShot))?.Id == repaired.Id, "failed resolution was cached on retry");

            await database.RemoveFolder((await database.GetFolders()).Single().Id);
            for (int round = 0; round < 30; round++)
            {
                var imports = new Task<Music?>[4];
                for (int i = 0; i < imports.Length; i++)
                    imports[i] = database.AddExternalFileAsync(new Music
                    {
                        Path = Path.Combine(root, $"parallel-{i}.mp3"), FolderPath = root
                    }, "");
                Check((await Task.WhenAll(imports)).All(m => m is { Id: > 0 }), "concurrent import failed");
                var rows = await database.GetFoldersWithSongCountsAsync();
                Check(rows.Count == 1 && rows[0].SongCount == 4, "concurrent imports duplicated virtual folder");
                await database.RemoveFolder(rows[0].Id);
            }
            Console.WriteLine("PASS: real one-shot resolver retries removed/failed identities; first publication, idempotent counts and concurrent virtual-folder creation.");
        }
        finally
        {
            await folders.StopAsync();
            await database.Connection.CloseAsync();
        }

        static Task<Music?> ResolvedAsync(OneShotPlaybackService service) =>
            ((Task<Music?>)typeof(OneShotPlaybackService).GetField("_resolveTask",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(service)!)
                .WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task StartupCancellationAsync(string root)
    {
        var database = new MusicDatabaseService(Path.Combine(root, "startup-cancel.db"));
        await database.InitializeAsync();
        App.Services = new ServiceCollection().AddSingleton(database).BuildServiceProvider();
        string directory = Path.Combine(root, "StartupCancel");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "slow.mp3"), "");
        var missing = new Music { Path = Path.Combine(directory, "missing.mp3"), FolderPath = directory };
        await database.Connection.InsertAsync(missing);
        await database.Connection.InsertAsync(new Folder { Path = directory });
        ToolUtils.SlowFile = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ToolUtils.SlowFileEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var scan = InitialFileScan.InitialScan(stop.Token);
        try
        {
            await ToolUtils.SlowFileEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            stop.Cancel();
            await Task.Delay(100);
            Check(!scan.IsCompleted, "cancellation abandoned an in-flight metadata read");
            ToolUtils.SlowFile.TrySetResult();
            try { await scan.WaitAsync(TimeSpan.FromSeconds(3)); throw new Exception("startup swallowed cancellation"); }
            catch (OperationCanceledException) { }
            var songs = await database.GetMusicListAsync();
            Check(songs.Count == 1 && songs[0].Id == missing.Id,
                "cancelled startup committed late metadata or inferred deletions");
            await InitialFileScan.InitialScan();
            songs = await database.GetMusicListAsync();
            Check(songs.Count == 1 && songs[0].Path.EndsWith("slow.mp3"), "cancelled scan could not retry");
            // 插入时已保存精确文件时间，紧随的扫描应报告无变更。
            Check(!await InitialFileScan.InitialScan(), "unchanged scan reported database changes");
            // 外部改标签（仅时间戳变化、无增删）也必须报告有变更，启动扫描才会刷新 UI。
            File.SetLastWriteTime(songs[0].Path, File.GetLastWriteTime(songs[0].Path).AddSeconds(5));
            Check(await InitialFileScan.InitialScan(), "metadata update scan reported no changes");
            Check(!await InitialFileScan.InitialScan(), "scan after update reported database changes");
            string secondDirectory = Path.Combine(root, "StartupProgress");
            Directory.CreateDirectory(secondDirectory);
            await database.Connection.InsertAsync(new Folder { Path = secondDirectory });
            for (int i = 0; i < 9; i++) File.WriteAllText(Path.Combine(secondDirectory, $"{i}.mp3"), "");
            var progress = new ScanProgress();
            await InitialFileScan.InitialScan(progress: progress);
            Check(progress.Values[0] == 0 && progress.Values.Contains(10) && progress.Values[^1] == 99,
                "file progress did not count unchanged files against the total across folders");
            Check(progress.Values.Count <= 100 && progress.Values.Zip(progress.Values.Skip(1)).All(p => p.First < p.Second),
                "scan progress was duplicated, regressed or flooded notifications");
            Check((await database.GetMusicListAsync()).Count == 10, "progress scanning lost files");
            Console.WriteLine("PASS: file-weighted progress across folders, unchanged files, monotonic bounded reports.");
            Console.WriteLine("PASS: startup cancellation joins the active read, avoids late commit/deletion, and permits retry.");
        }
        finally
        {
            ToolUtils.SlowFile.TrySetResult();
            try { await scan; } catch (OperationCanceledException) { }
            await database.Connection.CloseAsync();
        }
    }

    private static void PlaybackSnapshot()
    {
        var playing = new Music { Id = 1, Title = "playing" };
        var next = new Music { Id = 2, Title = "old metadata" };
        var removed = new Music { Id = 3 };
        var updated = new Music { Id = 2, Title = "new metadata" };
        var added = new Music { Id = 4 };
        var songs = new Dictionary<int, Music> { [2] = updated, [4] = added };
        var queue = new System.Collections.ObjectModel.ObservableCollection<Music> { removed, next, playing };
        int notifications = 0;
        queue.CollectionChanged += (_, _) => notifications++;
        LibraryPlaybackReconciler.Reconcile(queue, songs, playing);
        Check(queue.Count == 2 && ReferenceEquals(queue[0], updated) && ReferenceEquals(queue[1], playing),
            "scan reset queue order/current object or inserted unrequested songs");
        Check(notifications == 2, "queue did not publish replace/remove notifications");
        LibraryPlaybackReconciler.Reconcile(queue, songs, playing);
        Check(notifications == 2, "unchanged reconciliation emitted notifications");
        songs.Clear();
        LibraryPlaybackReconciler.Reconcile(queue, songs, playing);
        Check(queue.Count == 1 && ReferenceEquals(queue[0], playing), "deleted active track was interrupted");
        LibraryPlaybackReconciler.Reconcile(queue, songs, null);
        Check(queue.Count == 0, "empty library retained stale inactive tracks");
        Console.WriteLine("PASS: queue reconciliation preserves current object/order and publishes only necessary changes.");
    }

    private static async Task BenchmarkAsync()
    {
        const int size = 100_000;
        await ScanPipeline.RunAsync(Enumerable.Range(0, 1000), (i, _) => ValueTask.FromResult(i), _ => Task.CompletedTask, TimeSpan.Zero);
        long bytes = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        using var semaphore = new SemaphoreSlim(8);
        var legacy = Enumerable.Range(0, size).Select(async i =>
        {
            await semaphore.WaitAsync();
            try { return i; }
            finally { semaphore.Release(); }
        }).ToList();
        await Task.WhenAll(legacy);
        long oldBytes = GC.GetTotalAllocatedBytes(true) - bytes;
        double oldMs = watch.Elapsed.TotalMilliseconds;
        bytes = GC.GetTotalAllocatedBytes(true);
        watch.Restart();
        int total = 0;
        await ScanPipeline.RunAsync(Enumerable.Range(0, size), (i, _) => ValueTask.FromResult(i), batch =>
        {
            total += batch.Count;
            return Task.CompletedTask;
        }, TimeSpan.Zero);
        long newBytes = GC.GetTotalAllocatedBytes(true) - bytes;
        Check(total == size, "benchmark lost results");
        Console.WriteLine($"Synthetic scheduling ({size:N0} results): legacy {oldBytes:N0} B / {oldMs:F1} ms; pipeline {newBytes:N0} B / {watch.Elapsed.TotalMilliseconds:F1} ms. Excludes ATL/SQLite/UI.");
    }

    private static void ProbeAudio(string root)
    {
        string nativeRoot = Path.GetFullPath("bin/x64/Debug/net11.0-windows10.0.26100.0/win-x64");
        Check(File.Exists(Path.Combine(nativeRoot, "avformat-63.dll")), "build native FFmpeg binaries first");
        FFmpeg.AutoGen.ffmpeg.RootPath = nativeRoot;
        string wav = Path.Combine(root, "音频.wav");
        using (var writer = new BinaryWriter(File.Create(wav)))
        {
            const int size = 48000 * 2 * 2;
            writer.Write("RIFF"u8); writer.Write(size + 36); writer.Write("WAVEfmt "u8);
            writer.Write(16); writer.Write((short)1); writer.Write((short)2); writer.Write(48000);
            writer.Write(48000 * 4); writer.Write((short)4); writer.Write((short)16);
            writer.Write("data"u8); writer.Write(size); writer.Write(new byte[size]);
        }
        Check(FFmpegMetadataProbe.TryProbe(wav, out var info), "native FFmpeg probe failed");
        Check(info.SampleRate == 48000 && info.Channels == 2 && info.BitDepth == 16 && Math.Abs(info.DurationMs - 1000) < 1, "incorrect probe fields/units");
        string corrupt = Path.Combine(root, "corrupt.mp3");
        File.WriteAllText(corrupt, "not audio");
        Check(!FFmpegMetadataProbe.TryProbe(corrupt, out _), "corrupt file accepted");
        Check(!FFmpegMetadataProbe.TryProbe("https://example.invalid/test.mp3", out _), "network input accepted");
        Check(!FFmpegMetadataProbe.TryProbe(Path.Combine(root, "missing.mp3"), out _), "missing file accepted");
        File.Move(wav, wav + ".released");
        Console.WriteLine("PASS: native FFmpeg Unicode WAV/units, corrupt/missing files, local-only paths and handle release.");
    }
}
