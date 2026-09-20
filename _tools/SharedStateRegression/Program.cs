using System.Collections.Concurrent;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;
using static WinUIMusicPlayer.Utils.ToolUtils;

using var context = new TestContext();
SynchronizationContext.SetSynchronizationContext(context);
var tests = RunAsync();
while (!tests.IsCompleted) context.Pump();
await tests;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task RunAsync()
{
    var state = new DesktopLyricsState();
    int firstPage = 0, secondPage = 0;
    state.PropertyChanged += (_, _) => firstPage++;
    state.PropertyChanged += (_, _) => secondPage++;
    state.IsKaraokeEnabled = false;
    state.IsKaraokeEnabled = false;
    Check(firstPage == 1 && secondPage == 1, "Shared value must notify both pages exactly once.");

    var queue = new PlaybackQueueState();
    var first = new Music(1, "one.flac");
    var external = new Music(0, "external.flac");
    queue.Replace([first, external]);
    queue.Mode = PlayMode.RandomLoop;
    queue.InsertNext(first, [external, new(0, "another.flac")]);
    Check(queue.Playing.Count == 4 && queue.Sequential.Count == 4, "Random additions must reach canonical queue.");
    queue.Mode = PlayMode.ListLoop;
    Check(queue.Playing.Count == 4, "Mode switch must preserve duplicates and additions.");
    var restored = new PlaybackQueueState();
    restored.Replace(new(queue.Sequential));
    Check(restored.Playing.Count == 4, "Canonical persistence must restore appended members.");
    Check(restored.IndexOf(null) == -1 && restored.IndexOf(new(0, "missing.flac")) == -1, "Null and Id=0 paths must remain distinct.");
    Console.WriteLine("PASS: shared notifications; random append/mode switch/restore; duplicates and external identities.");

    int writes = 0, value = 0, saved = 0;
    var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var saves = new SettingsSaveQueue(async () =>
    {
        writes++;
        int snapshot = value;
        if (writes == 1) { firstWrite.SetResult(); await gate.Task; }
        saved = snapshot;
    });
    Task pending = Task.CompletedTask;
    for (int i = 0; i < 1000; i++) { value = i; pending = saves.RequestAsync(); }
    await firstWrite.Task;
    Check(writes == 1, "One UI burst should build one snapshot.");
    value = 2000;
    Check(ReferenceEquals(pending, saves.RequestAsync()), "Edits during I/O must share the same barrier.");
    gate.SetResult();
    await saves.FlushAsync();
    Check(writes == 2 && saved == 2000, "Flush must include edits made during slow I/O.");

    bool fail = true;
    var retry = new SettingsSaveQueue(() => fail ? Task.FromException(new IOException("test")) : Task.CompletedTask);
    try { await retry.RequestAsync(); throw new Exception("Failure lost"); } catch (IOException) { }
    fail = false;
    await retry.FlushAsync();
    Console.WriteLine("PASS: 1000 edits coalesced; slow-write final flush; failed write remains retryable.");

    var lifecycle = new AppLifecycle();
    var tasks = new ApplicationTasks(lifecycle);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var actualCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var work = tasks.RunAsync(async token => { started.SetResult(); await actualCompletion.Task; });
    await started.Task;
    lifecycle.TryBeginExit(out _);
    var drain = tasks.DrainAsync();
    Check(!drain.IsCompleted, "Cancellation must not pretend the underlying operation completed.");
    bool lateRan = false;
    await tasks.RunAsync(_ => { lateRan = true; return Task.CompletedTask; });
    Check(!lateRan, "Late work must not start.");
    actualCompletion.SetResult();
    await Task.WhenAll(work, drain);
    Console.WriteLine("PASS: stopping rejects late work and waits for real completion.");

    string tempRoot = Path.GetFullPath(Path.GetTempPath());
    string fixture = Path.Combine(tempRoot, "SharedStateRegression-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(fixture);
    try
    {
        string original = Path.Combine(fixture, "original.wav");
        await File.WriteAllTextAsync(original, "source");
        var converter = new AudioConverterService();
        var writer = new WinUIMusicPlayer.Helper.UsbWriterHelper(converter, new MusicDatabaseService());
        var device = new UsbStorageDevice { Name = "test", Path = Path.Combine(fixture, "usb"), UniqueId = "test" };
        var results = await writer.WriteToUsb([new(1, original), new(2, Path.Combine(fixture, "missing.wav"))], device, "mp3");
        Check(results.Count == 1 && results[0].Extension == "wav", "Failed conversion must record the copied source extension; missing sources must not succeed.");
        converter.Succeed = true;
        results = await writer.WriteToUsb([new(1, original)], device, "mp3");
        Check(results.Count == 1 && results[0].Extension == "mp3", "Successful conversion must record its actual output.");
        results = await writer.WriteToUsb([new(1, original)], device, cancellationToken: new CancellationToken(true));
        Check(results.Count == 0, "Cancelled batch must not claim successful copies.");
    }
    finally
    {
        if (!Path.GetFullPath(fixture).StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture path escaped temp root.");
        Directory.Delete(fixture, recursive: true);
    }
    Console.WriteLine("PASS: real file copy; missing source; conversion fallback/success use actual extension; cancellation.");

    var statsService = new PlaybackStatsService();
    var shutdown = new ShutdownCoordinator(new AppLifecycle(), Microsoft.Extensions.Logging.Abstractions.NullLogger<ShutdownCoordinator>.Instance);
    var stats = new WinUIMusicPlayer.ViewModel.StatsViewModel(new(), statsService,
        Microsoft.Extensions.Logging.Abstractions.NullLogger<WinUIMusicPlayer.ViewModel.StatsViewModel>.Instance, shutdown);
    int uiThread = Environment.CurrentManagedThreadId;
    stats.PropertyChanged += (_, _) => Check(Environment.CurrentManagedThreadId == uiThread, "Stats must publish on UI context.");
    stats.OnPageActive();
    var timer = WinUIMusicPlayer.App.MainWindow.DispatcherQueue.Timer;
    timer.Fire();
    Check(statsService.Queries.Count == 1, "Initial stats query missing.");
    stats.SelectedTimeRangeIndex = (int)WinUIMusicPlayer.ViewModel.StatsRange.PastYear;
    timer.Fire();
    statsService.Queries[0].SetResult(new() { TracksPlayedCount = 7 });
    while (statsService.Queries.Count < 2) await Task.Yield();
    Check(stats.TracksPlayedCount == 0, "Stale stats result was published.");
    statsService.Queries[1].SetResult(new() { TracksPlayedCount = 365 });
    while (stats.IsLoading) await Task.Yield();
    Check(stats.TracksPlayedCount == 365 && statsService.HeatmapQueries == 0, "Latest range should win and reuse its own annual heatmap.");
    stats.OnPageInactive();
    statsService.Notify();
    timer.Fire();
    await Task.Yield();
    Check(statsService.Queries.Count == 2, "Inactive stats page must not issue queries.");
    stats.OnPageActive();
    timer.Fire();
    stats.OnPageInactive();
    statsService.Queries[2].SetResult(new() { TracksPlayedCount = 999 });
    await stats.StopAsync();
    Check(stats.TracksPlayedCount == 365, "Late inactive result must not overwrite displayed state.");
    Console.WriteLine("PASS: production StatsViewModel latest range, stale suppression, heatmap reuse, inactive lifecycle and UI notifications.");

    var library = new AppState();
    library.Browse.SelectedSortOption = new() { Tag = "A-Z" };
    library.Library.Replace([new(1, "b.flac") { Title = "needle" }, new(2, "a.flac") { Title = "A" }]);
    var queries = new LibraryQueries(library.Library);
    var projections = new LibraryProjectionService(library, queries, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var list = new BulkObservableCollection<Music>();
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.FillCount == 1 && list[0].Title == "A", "Unchanged query must reuse its published projection.");
    library.Browse.SearchText = "needle";
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.FillCount == 2 && list.Count == 1 && list[0].Title == "needle", "Search change must invalidate projection.");
    library.Library.Replace([new(3, "new.flac") { Title = "needle-new" }]);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.FillCount == 3 && list[0].Id == 3 && queries.FindById(1) is null, "Library replacement must invalidate projection and index.");
    library.Browse.SearchText = "";
    library.Browse.CurrentAlbumObj = new(4, "") { Album = "first" };
    library.Library.Replace([new(5, "1") { Album = "first" }, new(6, "2") { Album = "second" }]);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.Album, m => m.Album == library.Browse.CurrentAlbumObj.Album);
    library.Browse.CurrentAlbumObj = new(7, "") { Album = "second" };
    await projections.UpdateSongCollectionsAsync(list, SongViewType.Album, m => m.Album == library.Browse.CurrentAlbumObj.Album);
    Check(list.Count == 1 && list[0].Id == 6, "Detail selection must be part of the query key.");
    Console.WriteLine("PASS: production library projection cache, search/group/version invalidation and index refresh.");
}

sealed class TestContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public void Pump()
    {
        if (_queue.TryTake(out var work, 1000)) work.Callback(work.State);
    }
    public void Dispose() => _queue.Dispose();
}
