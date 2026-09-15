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
            var vm = new AddFolderViewModel(database, appVm, NullLogger<AddFolderViewModel>.Instance);
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
