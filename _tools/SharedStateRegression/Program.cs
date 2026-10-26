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

    var keys = new HotKeyState();
    int keyNotifications = 0;
    keys.PropertyChanged += (_, _) => keyNotifications++;
    var shortcut = new List<string> { "Ctrl", "P" };
    keys.PlayOrPauseShortcut = shortcut;
    keys.PlayOrPauseShortcut = shortcut;
    Check(keyNotifications == 1, "Shared shortcuts should not notify an unchanged reference twice.");
    var editors = new EditorSessions();
    int closedEditorStops = 0, remainingEditorStops = 0;
    editors.Attach(() => { closedEditorStops++; return Task.CompletedTask; }).Dispose();
    var editorGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    editors.Attach(() => editorGate.Task);
    editors.Attach(() => Task.FromException(new IOException("editor failure")));
    editors.Attach(() => { remainingEditorStops++; return Task.CompletedTask; });
    var editorStop = editors.StopAsync();
    Check(!editorStop.IsCompleted, "Shutdown must wait for the actual editor task.");
    editorGate.SetResult();
    try { await editorStop; throw new Exception("Editor failure disappeared"); } catch (AggregateException) { }
    Check(closedEditorStops == 0 && remainingEditorStops == 1, "Unloaded editors must not be retained; one failure must not skip another editor.");
    await editors.StopAsync();
    Console.WriteLine("PASS: shared shortcut notifications; editor unsubscribe, actual drain, failure isolation and idempotence.");


    var tagState = new AppState();
    tagState.Library.Replace([
        new Music(1, "one.flac") { Title = "Z", Author = "B", AlbumArtist = "", Genre = "" },
        new Music(2, "two.flac") { Title = "A", Author = "Z", AlbumArtist = "A", Genre = "Jazz", Composer = "rare composer" },
        new Music(3, "three.flac") { Title = "B", Author = "B", AlbumArtist = "", Genre = " " },
        new Music(4, "four.flac") { Title = "C", Author = "A", AlbumArtist = "", Genre = "Classical", CatalogNumber = "CAT-rare" },
        new Music(5, "five.flac") { Title = "D", Author = "", AlbumArtist = "", Genre = "" }
    ]);
    var tagProjection = new LibraryProjectionService(tagState, new LibraryQueries(tagState.Library),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var tagList = new BulkObservableCollection<Music>();
    tagState.Browse.SelectedSortOption = new SortOption { Tag = "AlbumArtist" };
    await tagProjection.UpdateSongCollectionsAsync(tagList, SongViewType.All);
    Check(tagList.Select(m => m.Id).SequenceEqual([2, 4, 3, 1, 5]), "Album artist sort falls back to track artist and puts missing effective names last.");
    tagState.Browse.SelectedSortOption = new SortOption { Tag = "Genre" };
    await tagProjection.UpdateSongCollectionsAsync(tagList, SongViewType.All);
    Check(tagList.Select(m => m.Id).SequenceEqual([4, 2, 3, 5, 1]), "Genre sort puts absent/whitespace tags last and orders them by title.");
    tagState.Browse.SearchText = "RARE COMPOSER";
    await tagProjection.UpdateSongCollectionsAsync(tagList, SongViewType.All);
    Check(tagList.Count == 1 && tagList[0].Id == 2, "New tags participate in case-insensitive projected search.");
    Check(LibraryProjectionService.MusicMatchesSearch(tagState.Library.Songs[3], "CAT-rare"), "Playlist search uses the same additional metadata fields.");
    tagState.Browse.SearchText = "";
    tagState.Library.Replace(Enumerable.Range(0, 1000).Select(i => new Music(i + 1, i + ".flac") { Title = (1000 - i).ToString("D4"), Author = "" }).ToList());
    await tagProjection.UpdateSongCollectionsAsync(tagList, SongViewType.All);
    Check(tagList.Count == 1000 && tagList[0].Title == "0001" && tagList[^1].Title == "1000", "A library with no optional tags still has a deterministic useful order.");
    await tagProjection.StopAsync();
    Console.WriteLine("PASS: sparse optional metadata search, album artist fallback, unknown genres last, stable all-empty sorting.");

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

    var duplicates = new PlaybackQueueState();
    duplicates.Replace([first, first, external]);
    long secondEntry = duplicates.EntryIdAt(1);
    duplicates.SelectEntry(secondEntry, first);
    Check(duplicates.IndexOf(first) == 1, "Second occurrence must retain its cursor.");
    duplicates.InsertNext(first, [external]);
    Check(duplicates.EntryIdAt(1) == secondEntry && duplicates.Playing.Count == 4, "Insert must preserve occurrence identity.");
    duplicates.Mode = PlayMode.RandomLoop;
    Check(duplicates.EntryIdAt(duplicates.IndexOf(first)) == secondEntry, "Shuffle must keep the selected occurrence.");
    var preciseRestore = new PlaybackQueueState();
    preciseRestore.Replace(new(duplicates.Sequential));
    preciseRestore.Mode = PlayMode.RandomLoop;
    preciseRestore.RestoreEntries(duplicates.CaptureEntryIds(), duplicates.CaptureOrderIds(), duplicates.CaptureMusicIds(), secondEntry);
    Check(preciseRestore.CurrentEntryId == secondEntry && preciseRestore.EntryIdAt(preciseRestore.IndexOf(first)) == secondEntry, "Restore must preserve duplicate cursor and shuffle order.");
    Check(preciseRestore.CaptureOrderIds().SequenceEqual(duplicates.CaptureOrderIds()), "Random traversal order must survive restart.");
    preciseRestore.Mode = PlayMode.ListLoop;
    Check(preciseRestore.IndexOf(first) == 1, "Sequential mode must recover the same occurrence.");
    preciseRestore.Sequential.RemoveAt(0);
    Check(preciseRestore.IndexOf(first) == 0 && preciseRestore.CurrentEntryId == secondEntry, "Removing the first duplicate must preserve the second entry.");
    var refreshedExternal = new Music(0, "external.flac");
    preciseRestore.ReconcileLibrary(new Dictionary<int, Music> { [0] = refreshedExternal, [1] = first }, first);
    Check(preciseRestore.CurrentEntryId == secondEntry && preciseRestore.IndexOf(first) == 0, "Library refresh must preserve current entry identity.");
    Console.WriteLine("PASS: stable duplicate identity, remove, library refresh, shuffle order and precise restore.");

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
    stats.SelectedTimeRangeIndex = (int)WinUIMusicPlayer.ViewModel.StatsRange.AllTime;
    timer.Fire();
    while (statsService.Queries.Count < 3) await Task.Yield();
    Check(statsService.QueryRanges[2].Start == new DateTime(0, DateTimeKind.Utc), "All-time range must query from the UTC epoch floor to cover all history.");
    Check(statsService.QueryRanges[2].End.ToLocalTime() >= DateTime.Now.Date, "All-time range must cover up to today.");
    statsService.Queries[2].SetResult(new() { TracksPlayedCount = 1000 });
    while (stats.IsLoading) await Task.Yield();
    Check(stats.TracksPlayedCount == 1000 && statsService.HeatmapQueries == 0, "All-time must reuse snapshot daily counts for the annual heatmap instead of re-querying.");
    stats.OnPageInactive();
    statsService.Notify();
    timer.Fire();
    await Task.Yield();
    Check(statsService.Queries.Count == 3, "Inactive stats page must not issue queries.");
    stats.OnPageActive();
    timer.Fire();
    stats.OnPageInactive();
    statsService.Queries[3].SetResult(new() { TracksPlayedCount = 999 });
    await stats.StopAsync();
    Check(stats.TracksPlayedCount == 1000, "Late inactive result must not overwrite displayed state.");
    Console.WriteLine("PASS: production StatsViewModel latest range, all-time floor, stale suppression, heatmap reuse, inactive lifecycle and UI notifications.");

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
    library.Browse.SearchText = "";
    await Task.Run(() => projections.UpdateSongCollectionsAsync(list, SongViewType.All));
    Check(list.Count == 2, "Background callers must marshal capture and publication to the UI owner.");
    library.Browse.SearchText = "missing";
    var superseded = projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    library.Browse.SearchText = "";
    var latest = projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(ReferenceEquals(superseded, latest), "One target must share one active refresh barrier.");
    await latest;
    Check(list.Count == 2, "Latest pending search must win.");
    library.Library.Replace([
        new(10, "local") { SourceId = 0, Album = "same album", Title = "track 1", TrackNumber = 1 },
        new(11, "remote-a") { SourceId = 1, Album = "same album", Title = "track 1", TrackNumber = 1 },
        new(12, "remote-b") { SourceId = 2, Album = "same album", Title = "network-only track", TrackNumber = 2 },
        new(13, "remote-c") { SourceId = 2, Album = "other album", Title = "track 3", TrackNumber = 1 }
    ]);
    library.Browse.SourceFilterId = 1;
    queries.SetSourceFilter(1);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.Count == 1 && list[0].Id == 11 && queries.FindById(10) is not null, "Source filtering must preserve the global queue ID index.");
    library.Browse.SourceFilterId = -2;
    queries.SetSourceFilter(-2);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.Count == 3 && list.All(m => m.IsRemote), "All remote sources excludes local songs.");
    library.Browse.SourceFilterId = -1;
    queries.SetSourceFilter(-1);
    projections.UpdateGroupedByFirstLetter(m => m.Album, m => "A", library.LibraryViews.AlbumPageSource);
    await Task.Yield();
    var albumGroups = (List<MusicGroup>)library.LibraryViews.AlbumPageSource.Source!;
    Check(albumGroups.Sum(g => g.Items.Count) == 2, "Mirrored albums should produce one album card.");
    Check(albumGroups.SelectMany(g => g.Items).Single(m => m.Album == "same album").SourceId == 0,
        "The local copy should represent a mirrored album when all sources are shown.");
    Check(queries.GetAlbumSongCount("same album") == 3, "Album song counts should include tracks from every source.");
    await projections.UpdateSongCollectionsAsync(list, SongViewType.Album, m => m.Album == "same album");
    Check(list.Count == 3 && list.Any(m => m.Id == 10) && list.Any(m => m.Id == 11) && list.Any(m => m.Id == 12),
        "Album details should include every track from every source, including same-metadata tracks.");
    library.Browse.SearchText = "missing";
    var pendingProjection = projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    int fillsBeforeStop = list.FillCount;
    await projections.StopAsync();
    await pendingProjection;
    Check(list.FillCount == fillsBeforeStop, "Stopping must suppress delayed list publication.");
    Console.WriteLine("PASS: library cache, version invalidation, shared refresh barrier, latest search and stop suppression.");

    // 原始文件名检索：标签字段不含搜索词时按文件名命中；目录段不属于文件名。
    var nameState = new AppState();
    nameState.Library.Replace([
        new(20, @"D:\Music\Live\needle-file.flac") { Title = "unrelated" },
        new(21, "webdav://1/dav/My%20Needle%20Song.flac") { Title = "unrelated", SourceId = 1 },
        new(22, @"D:\needle\decoy.flac") { Title = "unrelated" }
    ]);
    var nameProjections = new LibraryProjectionService(nameState, new LibraryQueries(nameState.Library),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var nameList = new BulkObservableCollection<Music>();
    nameState.Browse.SearchText = "needle-FILE";
    await nameProjections.UpdateSongCollectionsAsync(nameList, SongViewType.All);
    Check(nameList.Count == 1 && nameList[0].Id == 20, "Original file names must match case-insensitively.");
    nameState.Browse.SearchText = "%20Needle%20";
    await nameProjections.UpdateSongCollectionsAsync(nameList, SongViewType.All);
    Check(nameList.Count == 1 && nameList[0].Id == 21, "Raw URL-encoded remote file names must match.");
    nameState.Browse.SearchText = "My Needle Song";
    await nameProjections.UpdateSongCollectionsAsync(nameList, SongViewType.All);
    Check(nameList.Count == 1 && nameList[0].Id == 21, "Remote file names must match after percent-decoding.");
    nameState.Browse.SearchText = "decoy";
    await nameProjections.UpdateSongCollectionsAsync(nameList, SongViewType.All);
    Check(nameList.Count == 1 && nameList[0].Id == 22, "File name match must include the extension.");
    nameState.Browse.SearchText = @"\needle\";
    await nameProjections.UpdateSongCollectionsAsync(nameList, SongViewType.All);
    Check(nameList.Count == 0, "Directory segments must not match as file names.");
    Console.WriteLine("PASS: original file name search covers case, remote encoding and directory boundaries.");
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
