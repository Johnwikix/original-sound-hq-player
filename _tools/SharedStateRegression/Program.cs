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

    var reorder = new PlaybackQueueState();
    var reorderA = new Music(21, "reorder-a.flac");
    var reorderB = new Music(22, "reorder-b.flac");
    var reorderC = new Music(23, "reorder-c.flac");
    reorder.Replace([reorderA, reorderB, reorderC]);
    long pinnedEntry = reorder.EntryIdAt(0);
    reorder.SetRequestedVersion(pinnedEntry, 7);
    long[] canonicalBefore = reorder.CaptureEntryIds();
    reorder.BeginReorder();
    var reorderVisible = reorder.Playing;
    var draggedMusic = reorderVisible[0];
    reorderVisible.RemoveAt(0);
    reorderVisible.Insert(2, draggedMusic);
    reorder.EndReorder();
    Check(reorder.EntryIdAt(2) == pinnedEntry && reorder.RequestedVersion(pinnedEntry) == 7,
        "Drag reorder must keep the moved occurrence identity and pinned version.");
    Check(reorder.CaptureEntryIds().SequenceEqual(canonicalBefore.Skip(1).Append(canonicalBefore[0])),
        "Sequential drag must rewrite the canonical order.");
    Check(reorder.CaptureOrderIds().SequenceEqual(reorder.CaptureEntryIds()),
        "Sequential traversal must follow the reordered canonical queue.");
    long[] afterDrag = reorder.CaptureOrderIds();
    reorder.BeginReorder();
    reorder.EndReorder();
    Check(reorder.CaptureOrderIds().SequenceEqual(afterDrag), "A cancelled drag must leave the queue untouched.");
    Console.WriteLine("PASS: sequential drag keeps occurrence identity, pinned version and cancelled drag is inert.");

    var randomReorder = new PlaybackQueueState();
    randomReorder.Replace([reorderA, reorderB, reorderC]);
    randomReorder.Mode = PlayMode.RandomLoop;
    long[] randomCanonical = randomReorder.CaptureEntryIds();
    long[] randomOrder = randomReorder.CaptureOrderIds();
    randomReorder.BeginReorder();
    var randomVisible = randomReorder.Playing;
    var randomDragged = randomVisible[0];
    randomVisible.RemoveAt(0);
    randomVisible.Insert(2, randomDragged);
    randomReorder.EndReorder();
    Check(randomReorder.CaptureOrderIds().SequenceEqual(randomOrder.Skip(1).Append(randomOrder[0])),
        "Random drag must reorder the traversal order.");
    Check(randomReorder.EntryIdAt(2) == randomOrder[0], "Random drag must carry the moved occurrence identity.");
    Check(randomReorder.CaptureEntryIds().SequenceEqual(randomCanonical), "Random drag must not rewrite the canonical queue.");
    Console.WriteLine("PASS: random drag reorders only the traversal order.");

    foreach (bool shuffle in new[] { false, true })
    {
        var repeated = new PlaybackQueueState();
        repeated.Replace([reorderA, reorderA, reorderB], playlistEntries: [31, 32, 33], requestedVersions: [11, 12, 13]);
        var ids = repeated.CaptureEntryIds();
        if (shuffle)
        {
            repeated.Mode = PlayMode.RandomLoop;
            repeated.RestoreEntries(ids, [ids[1], ids[0], ids[2]], repeated.CaptureMusicIds(), ids[0],
                [11, 12, 13], playlistEntries: [31, 32, 33]);
        }
        long dragged = repeated.EntryIdAt(0);
        long neighbour = repeated.EntryIdAt(1);
        repeated.SelectEntry(dragged, reorderA);
        var dragOwner = new object();
        repeated.BeginReorder(dragOwner);
        repeated.EndReorder(new object());
        Check(repeated.IsReordering, "Closing another queue view ended the active drag.");
        repeated.Playing.RemoveAt(0);
        repeated.Playing.Insert(1, reorderA);
        repeated.EndReorder();
        Check(repeated.EntryIdAt(1) == dragged && repeated.EntryIdAt(0) == neighbour
            && repeated.IndexOf(reorderA) == 1 && repeated.RequestedVersion(dragged) == (shuffle ? 12 : 11),
            "Dragging identical song references must move the selected occurrence with its pinned version.");
        long version = repeated.Version;
        repeated.BeginReorder();
        repeated.EndReorder();
        Check(repeated.Version == version, "A cancelled drag must not invalidate gapless or save a new queue.");
        Check(!shuffle || repeated.CaptureEntryIds().SequenceEqual(ids), "Random reorder changed the canonical queue.");
        repeated.BeginReorder();
        repeated.Playing.Move(1, 0);
        repeated.EndReorder();
        Check(repeated.EntryIdAt(0) == dragged, "A native Move must preserve the occurrence identity.");
        repeated.BeginReorder();
        repeated.Playing.Insert(2, reorderA);
        repeated.Playing.RemoveAt(0);
        repeated.EndReorder();
        Check(repeated.EntryIdAt(1) == dragged, "Insert-first reorder must account for the shifted removal index.");
        repeated.BeginReorder();
        repeated.Playing.RemoveAt(0);
        repeated.EndReorder();
        Check(repeated.EntryIdAt(0) == dragged && repeated.PlaylistEntry(neighbour) == 0,
            "An unpaired removal must prune only the removed occurrence.");
    }
    Console.WriteLine("PASS: duplicate drag preserves occurrence identity in sequential and shuffled playback.");

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
        Check(WinUIMusicPlayer.Helper.UsbExportPaths.GetFolderName("Sony Music Labels Inc.") == "Sony Music Labels Inc",
            "USB export must trim trailing dots from folder names before calling Windows.Storage.");
        Check(WinUIMusicPlayer.Helper.UsbExportPaths.GetFolderName("攻殻機動隊 新劇場版 O.S.T. ") == "攻殻機動隊 新劇場版 O.S.T",
            "USB export must trim trailing spaces and dots together.");
        Check(WinUIMusicPlayer.Helper.UsbExportPaths.GetFolderName(".. ") == "_"
            && WinUIMusicPlayer.Helper.UsbExportPaths.GetFolderName("CON") == "_CON"
            && WinUIMusicPlayer.Helper.UsbExportPaths.GetFolderName("COM².mix") == "_COM².mix"
            && WinUIMusicPlayer.Helper.UsbExportPaths.GetFileName("lulu..flac") == "lulu..flac",
            "USB path normalization must handle traversal/reserved names without altering valid internal dots.");
        string original = Path.Combine(fixture, "original.wav");
        await File.WriteAllTextAsync(original, "source");
        var converter = new AudioConverterService();
        var writer = new WinUIMusicPlayer.Helper.UsbWriterHelper(converter, new MusicDatabaseService());
        var device = new UsbStorageDevice { Name = "test", Path = Path.Combine(fixture, "usb"), UniqueId = "test" };
        var results = await writer.WriteToUsb([new(1, original), new(2, Path.Combine(fixture, "missing.wav"))], device, "mp3");
        Check(results.Succeeded.Count == 1 && results.Succeeded[0].Extension == "wav", "Failed conversion must record the copied source extension; missing sources must not succeed.");
        Check(results.UnsentCount == 1 && results.Failures.Count == 1 && results.Failures[0].Stage == UsbExportFailureStage.Audio,
            "Partial audio failures must have exact counts and failed items.");
        converter.Succeed = true;
        results = await writer.WriteToUsb([new(1, original)], device, "mp3");
        Check(results.Succeeded.Count == 1 && results.Succeeded[0].Extension == "mp3", "Successful conversion must record its actual output.");
        results = await writer.WriteToUsb([new(1, original)], device, cancellationToken: new CancellationToken(true));
        Check(results.Succeeded.Count == 0, "Cancelled batch must not claim successful copies.");
        var portable = new UsbStorageDevice { Name = "unavailable", PortableDeviceId = "test", UniqueId = "test" };
        WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenCalls = 0;
        WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenError = new DirectoryNotFoundException();
        results = await writer.WriteToUsb([new(1, original), new(2, original)], portable);
        Check(results.Succeeded.Count == 0 && WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenCalls == 1,
            "Unavailable MTP storage must stop the batch rather than repeating a native timeout per song.");
        WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenCalls = 0;
        WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenError = new IOException("MTP",
            new System.Runtime.InteropServices.COMException("Device hung", unchecked((int)0x802A0006)));
        results = await writer.WriteToUsb([new(1, original), new(2, original)], portable);
        Check(results.Succeeded.Count == 0 && WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenCalls == 1,
            "A wrapped WPD device-hung error must stop the batch.");
        WinUIMusicPlayer.Helper.UsbPortableStorageWriter.OpenError = null;

        var failedLyrics = new MusicDatabaseService { FailLyrics = true };
        var lyricsWriter = new WinUIMusicPlayer.Helper.UsbWriterHelper(converter, failedLyrics);
        int copiedCallbacks = 0;
        results = await lyricsWriter.WriteToUsb([new(1, original)], device,
            onFileCopied: (_, _, _) => { copiedCallbacks++; return Task.CompletedTask; });
        Check(results.Succeeded.Count == 1 && results.UnsentCount == 0 && results.LyricsFailureCount == 1 && copiedCallbacks == 1,
            "A lyric failure must retain audio success and publish its icon once.");
        results = await writer.WriteToUsb([new(1, original)], device,
            onFileCopied: (_, _, _) => throw new IOException("Fixture UI publication failed"));
        Check(results.Succeeded.Count == 1 && results.UnsentCount == 0 && results.StatusFailureCount == 1,
            "An icon/status callback failure must not claim the audio copy failed.");

        string secondFile = Path.Combine(fixture, "second.wav");
        await File.WriteAllTextAsync(secondFile, "second");
        var firstRow = new Music(11, original) { Title = "first" };
        var secondRow = new Music(12, secondFile) { Title = "second" };
        var unchangedRow = new Music(13, original) { Title = "unrelated", IsExistOnDevice = 2 };
        var displayedRows = new List<Music> { firstRow, secondRow, unchangedRow };
        var ledger = new List<UsbDeviceMusic>();
        var firstPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = SynchronizationContext.Current!;
        int usbUiThread = Environment.CurrentManagedThreadId;
        int published = 0, unchangedNotifications = 0;
        unchangedRow.PropertyChanged += (_, _) => unchangedNotifications++;
        foreach (var row in displayedRows)
            row.PropertyChanged += (_, _) => Check(Environment.CurrentManagedThreadId == usbUiThread, "Device icons must publish on the UI context.");
        async Task PublishCopied(Music music, string extension, UsbFileFingerprint fingerprint)
        {
            var publication = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ui.Post(_ =>
            {
                try
                {
                    ledger.Add(new UsbDeviceMusic { Title = music.Title, Author = music.Author, Album = music.Album, Extension = extension });
                    UsbDeviceMusicPresence.Update(displayedRows, ledger, [music.Title]);
                    published++;
                    publication.SetResult();
                    if (published == 1) firstPublished.SetResult();
                }
                catch (Exception ex) { publication.SetException(ex); firstPublished.TrySetException(ex); }
            }, null);
            await publication.Task;
            if (published == 1) await allowNext.Task;
        }
        var copying = Task.Run(() => writer.WriteToUsb([firstRow, secondRow], device, onFileCopied: PublishCopied));
        await firstPublished.Task;
        Check(!copying.IsCompleted && firstRow.IsExistOnDevice == 2 && secondRow.IsExistOnDevice == 0 && unchangedNotifications == 0,
            "The first icon must update before the batch ends without resetting unrelated icons.");
        allowNext.SetResult();
        results = await copying;
        Check(published == 2 && secondRow.IsExistOnDevice == 2 && results.Succeeded.Count == 2 && unchangedNotifications == 0,
            "Each copied song must publish exactly one targeted update.");
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
    Check(statsService.Queries.Count == 1, "Initial stats query must start immediately without a debounce timer.");
    stats.OnPageActive();
    Check(statsService.Queries.Count == 1, "Repeated activation must not start another query.");
    stats.SelectedTimeRangeIndex = (int)WinUIMusicPlayer.ViewModel.StatsRange.PastMonth;
    stats.SelectedTimeRangeIndex = (int)WinUIMusicPlayer.ViewModel.StatsRange.PastYear;
    Check(statsService.Queries.Count == 1, "Range changes must wait for the actual pending query instead of running in parallel.");
    Check(statsService.QueryTokens[0].IsCancellationRequested, "Changing range must cancel the old read request without completing the actual query await.");
    Check(stats.IsLoading, "Cancellation must wait for the real pending query boundary.");
    statsService.Queries[0].SetResult(new() { TracksPlayedCount = 7 });
    while (statsService.Queries.Count < 2) await Task.Yield();
    Check(stats.TracksPlayedCount == 0, "Stale stats result was published.");
    statsService.Queries[1].SetResult(new() { TracksPlayedCount = 365 });
    while (stats.IsLoading) await Task.Yield();
    Check(stats.TracksPlayedCount == 365 && statsService.HeatmapQueries == 0, "Latest range should win and reuse its own annual heatmap.");
    stats.SelectedTimeRangeIndex = (int)WinUIMusicPlayer.ViewModel.StatsRange.AllTime;
    Check(statsService.Queries.Count == 3, "Idle range changes must query immediately without a timer tick.");
    Check(statsService.QueryRanges[2].Start == new DateTime(0, DateTimeKind.Utc), "All-time range must query from the UTC epoch floor to cover all history.");
    Check(statsService.QueryRanges[2].End.ToLocalTime() >= DateTime.Now.Date, "All-time range must cover up to today.");
    statsService.Queries[2].SetResult(new() { TracksPlayedCount = 1000 });
    while (stats.IsLoading) await Task.Yield();
    Check(stats.TracksPlayedCount == 1000 && statsService.HeatmapQueries == 0, "All-time must reuse snapshot daily counts for the annual heatmap instead of re-querying.");
    await Task.Run(statsService.Notify);
    while (statsService.Queries.Count < 4) await Task.Yield();
    Check(stats.IsLoading, "A background stats update must dispatch an immediate load onto the UI context.");
    statsService.Queries[3].SetResult(new() { TracksPlayedCount = 1001 });
    while (stats.IsLoading) await Task.Yield();
    statsService.Notify();
    stats.OnPageInactive();
    statsService.Notify();
    await Task.Yield();
    Check(statsService.Queries.Count == 4, "Inactive stats page must ignore unsubscribed and already queued updates.");
    stats.OnPageActive();
    Check(statsService.Queries.Count == 5, "Reentry must query immediately.");
    stats.OnPageInactive();
    Check(statsService.QueryTokens[4].IsCancellationRequested, "Inactive page must signal cancellation to its read request.");
    stats.OnPageActive();
    Check(statsService.Queries.Count == 5, "Reentry during cancellation must keep waiting for the actual in-flight read.");
    statsService.Queries[4].SetResult(new() { TracksPlayedCount = 999 });
    while (statsService.Queries.Count < 6) await Task.Yield();
    Check(stats.TracksPlayedCount == 1001, "A canceled result must not publish after reentry.");
    statsService.Queries[5].SetException(new IOException("stats read failure"));
    while (stats.IsLoading) await Task.Yield();
    statsService.Notify();
    while (statsService.Queries.Count < 7) await Task.Yield();
    var statsStop = stats.StopAsync();
    Check(!statsStop.IsCompleted && statsService.QueryTokens[6].IsCancellationRequested, "Shutdown must cancel and drain the actual read.");
    statsService.Queries[6].SetResult(new() { TracksPlayedCount = 998 });
    await statsStop;
    await stats.StopAsync();
    Check(stats.TracksPlayedCount == 1001, "Late inactive result must not overwrite displayed state.");
    Console.WriteLine("PASS: production StatsViewModel immediate activation/range/update/retry, latest range, stale suppression, heatmap reuse, reentry, actual shutdown drain and UI notifications.");

    var library = new AppState();
    library.Browse.SelectedSortOption = new() { Tag = "A-Z" };
    library.Library.Replace([new(1, "b.flac") { Title = "needle" }, new(2, "a.flac") { Title = "A" }]);
    var queries = new LibraryQueries(library.Library);
    var projections = new LibraryProjectionService(library, queries, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var list = new BulkObservableCollection<Music>();
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.FillCount == 1 && list[0].Title == "A", "Unchanged query must reuse its published projection.");
    var notifications = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
    list.CollectionChanged += (_, change) => notifications.Add(change.Action);
    var untouched = list[0];
    var added = new Music(88, "watch.mp3") { Title = "Watch" };
    library.Library.Songs.Add(added);
    library.Library.NotifyChanged();
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list.Contains(added) && ReferenceEquals(list[0], untouched) &&
        notifications.SequenceEqual([System.Collections.Specialized.NotifyCollectionChangedAction.Add]), "Watcher additions must publish one Add.");
    notifications.Clear();
    added.IsMissing = true;
    library.Library.NotifyChanged();
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(!list.Contains(added) && queries.FindById(added.Id) == added &&
        notifications.SequenceEqual([System.Collections.Specialized.NotifyCollectionChangedAction.Remove]), "A missing local version leaves the browse list but remains indexed.");
    notifications.Clear();
    library.Library.Songs[library.Library.Songs.IndexOf(untouched)] = untouched with { Path = "updated.mp3" };
    library.Library.NotifyChanged();
    await projections.UpdateSongCollectionsAsync(list, SongViewType.All);
    Check(list[0].Path == "updated.mp3" &&
        notifications.SequenceEqual([System.Collections.Specialized.NotifyCollectionChangedAction.Replace]), "Metadata changes must replace only the affected row.");
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
    var albumGroups = (IEnumerable<MusicGroup>)library.LibraryViews.AlbumPageSource.Source!;
    Check(albumGroups.Sum(g => g.Count) == 2, "Mirrored albums should produce one album card.");
    Check(albumGroups.SelectMany(g => g).Single(m => m.Album == "same album").SourceId == 0,
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
    var groupedState = new WinUIMusicPlayer.State.AppState();
    groupedState.Library.Replace([
        new(701, "a.flac") { SongId = 71, EncodingKind = 1, Album = "Versions", Genre = "Jazz" },
        new(702, "b.mp3") { SongId = 71, EncodingKind = 2, SourceId = 9, Album = "Versions", Genre = "Rock" },
        new(703, "c.flac") { SongId = 72, EncodingKind = 1, Album = "Versions" }
    ]);
    var groupedQueries = new LibraryQueries(groupedState.Library);
    var groupedProjection = new LibraryProjectionService(groupedState, groupedQueries, Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var groupedList = new BulkObservableCollection<Music>();
    await groupedProjection.UpdateSongCollectionsAsync(groupedList, SongViewType.All);
    Check(groupedList.Count == 2 && groupedQueries.GetAlbumSongCount("Versions") == 2, "Grouped songs count once in library and albums.");
    groupedState.Browse.SearchText = "Rock";
    await groupedProjection.UpdateSongCollectionsAsync(groupedList, SongViewType.All);
    Check(groupedList.Count == 1 && groupedList[0].Id == 701, "Search matches alternate version tags while preserving the stable representative.");
    groupedState.Browse.SearchText = "";
    await groupedProjection.UpdateSongCollectionsAsync(groupedList, SongViewType.Folder);
    Check(groupedList.Count == 3, "Folder view preserves the concrete files.");
    Check(groupedQueries.GetMultipleVersions(71)?.Count == 2 && groupedQueries.GetMultipleVersions(72) is null,
        "home aggregation can reuse duplicate members and bypass singleton lists");
    AppData.AllPlayListMusics.AddRange([
        new(701, 25, 3) { Id = 81, SongId = 71 }, new(701, 25, 2) { Id = 82, SongId = 71, RequestedMusicId = 701 },
        new(702, 25, 1) { Id = 83, SongId = 71, RequestedMusicId = 702 }
    ]);
    var scopedItems = groupedProjection.GetPlaylistItems(25, null, 9);
    Check(scopedItems.Select(item => item.EntryId).SequenceEqual([81, 83]) && scopedItems.All(item => item.Music.Id == 702),
        "a filtered playlist resolves ordinary occurrences in scope and hides out-of-scope fixed files");
    groupedState.Browse.CurrentPlayListId = 25;
    var projectedPlaylist = new BulkObservableCollection<PlayListMusicItem>();
    int playlistNotifications = 0;
    projectedPlaylist.CollectionChanged += (_, _) => playlistNotifications++;
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    var initialOccurrences = projectedPlaylist.ToArray();
    Check(initialOccurrences.Select(item => item.EntryId).SequenceEqual([81, 82, 83]),
        "Automatic and fixed occurrences stay distinct in playlist order.");
    playlistNotifications = 0;
    groupedState.Browse.SearchText = "versions";
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(playlistNotifications == 0 && projectedPlaylist.Zip(initialOccurrences).All(pair => ReferenceEquals(pair.First, pair.Second)),
        "A changed query key must not reset an equivalent ordered playlist projection.");

    groupedState.Browse.SourceFilterId = 9;
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Select(item => item.EntryId).SequenceEqual([81, 83]) &&
        projectedPlaylist.All(item => item.Music.Id == 702), "Source changes must update fixed and automatic occurrences.");
    groupedState.Browse.SourceFilterId = -1;
    groupedState.Browse.SearchText = "missing playlist term";
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Count == 0, "A new search must clear nonmatching playlist rows.");
    groupedState.Browse.SearchText = "";
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Select(item => item.EntryId).SequenceEqual([81, 82, 83]), "Clearing search restores all occurrences.");

    var originalLink = AppData.AllPlayListMusics.Single(item => item.Id == 81);
    int originalLinkIndex = AppData.AllPlayListMusics.IndexOf(originalLink);
    AppData.AllPlayListMusics[originalLinkIndex] = originalLink with { Order = 0, RequestedMusicId = 701 };
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Select(item => item.EntryId).SequenceEqual([82, 83, 81]) &&
        projectedPlaylist[^1].RequestedMusicId == 701,
        "A playlist-only relationship edit and reorder must publish without a library version change.");
    AppData.AllPlayListMusics.RemoveAt(originalLinkIndex);
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Select(item => item.EntryId).SequenceEqual([82, 83]), "Relationship deletion drops its own occurrence.");
    AppData.AllPlayListMusics.Insert(originalLinkIndex, originalLink);
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(projectedPlaylist.Select(item => item.EntryId).SequenceEqual([81, 82, 83]), "Restoring a distinct occurrence updates the list.");
    var originalMusic = projectedPlaylist[0].Music;
    int modelChanges = 0;
    originalMusic.PropertyChanged += (_, _) => modelChanges++;
    originalMusic.IsExistOnDevice = 1;
    playlistNotifications = 0;
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(modelChanges == 1 && playlistNotifications == 0 && ReferenceEquals(projectedPlaylist[0].Music, originalMusic),
        "An in-place Music notification remains visible without resetting the playlist.");
    var replacementMusic = originalMusic with { Title = "New playlist title" };
    groupedState.Library.Replace([replacementMusic, groupedState.Library.Songs[1], groupedState.Library.Songs[2]]);
    groupedQueries.Invalidate();
    await groupedProjection.RefreshPlayListSongMapping(projectedPlaylist);
    Check(ReferenceEquals(projectedPlaylist[0].Music, replacementMusic) && ReferenceEquals(projectedPlaylist[1].Music, replacementMusic),
        "A replacement Music instance with the same database ID updates both occurrences.");
    Console.WriteLine("PASS: playlist equality, relationship edits, duplicates, search, source, and Music identity.");

    var localVersion = groupedState.Library.Songs[0];
    var remoteVersion = groupedState.Library.Songs[1];
    var versionQueue = new PlaybackQueueState();
    versionQueue.Replace([localVersion, localVersion]);
    long automaticEntry = versionQueue.EntryIdAt(0), fixedEntry = versionQueue.EntryIdAt(1);
    versionQueue.SetRequestedVersion(fixedEntry, localVersion.Id);
    localVersion.IsMissing = true;
    groupedQueries.Invalidate();
    Check(groupedQueries.FindDisplayVersion(71) == remoteVersion && groupedQueries.FindDisplayVersion(71, 0) == localVersion,
        "missing automatic representative switches only within its source filter");
    versionQueue.ReconcileLibrary(groupedQueries.GetIdIndex(), null, groupedQueries.FindDisplayVersion);
    Check(versionQueue.Playing.SequenceEqual([remoteVersion, localVersion]) && versionQueue.EntryIdAt(0) == automaticEntry,
        "refresh substitutes the automatic occurrence and keeps the fixed duplicate");
    versionQueue.Replace([localVersion, localVersion]);
    automaticEntry = versionQueue.EntryIdAt(0);
    versionQueue.SetRequestedVersion(versionQueue.EntryIdAt(1), localVersion.Id);
    versionQueue.SelectEntry(automaticEntry, localVersion);
    groupedState.Library.Replace([remoteVersion]);
    versionQueue.ReconcileLibrary(groupedQueries.GetIdIndex(), localVersion, groupedQueries.FindDisplayVersion);
    Check(versionQueue.IndexOf(localVersion) == 0 && versionQueue.CaptureMusicIds().SequenceEqual([remoteVersion.Id]),
        "removing the current file preserves the song cursor and saves the surviving file for restart");
    Check(versionQueue.Playing.SequenceEqual([remoteVersion]) && versionQueue.EntryIdAt(0) == automaticEntry,
        "removal keeps the automatic occurrence and its identity while dropping the detached fixed queue entry");
    await groupedProjection.StopAsync();
    Console.WriteLine("PASS: grouped library counts, alternate-info search and concrete folder versions.");

    // 年份排序：缺年份（0）始终排在末尾，同年内按专辑 → 光盘 → 音轨给出确定顺序。
    var yearState = new AppState();
    yearState.Library.Replace([
        new Music(30, "b2.flac") { Year = 2005, Album = "B album", DiskNumber = 1, TrackNumber = 2, Title = "b2" },
        new Music(31, "a1.flac") { Year = 2005, Album = "A album", DiskNumber = 1, TrackNumber = 1, Title = "a1" },
        new Music(32, "c1.flac") { Year = 1999, Album = "C album", DiskNumber = 1, TrackNumber = 1, Title = "c1" },
        new Music(33, "d1.flac") { Year = 0, Album = "D album", DiskNumber = 1, TrackNumber = 1, Title = "d1" },
        new Music(34, "a2.flac") { Year = 2005, Album = "A album", DiskNumber = 2, TrackNumber = 1, Title = "a2" }
    ]);
    var yearProjections = new LibraryProjectionService(yearState, new LibraryQueries(yearState.Library),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<LibraryProjectionService>.Instance);
    var yearList = new BulkObservableCollection<Music>();
    yearState.Browse.SelectedSortOption = new() { Tag = "YearASC" };
    await yearProjections.UpdateSongCollectionsAsync(yearList, SongViewType.All);
    Check(string.Join(",", yearList.Select(m => m.Id)) == "32,31,34,30,33",
        "Year ASC must order by year, then album/disc/track, and keep unknown years last.");
    yearState.Browse.SelectedSortOption = new() { Tag = "YearDESC" };
    await yearProjections.UpdateSongCollectionsAsync(yearList, SongViewType.All);
    Check(string.Join(",", yearList.Select(m => m.Id)) == "31,34,30,32,33",
        "Year DESC must reverse years but keep album track order and unknown years last.");
    yearState.Browse.SelectedSortOption = new() { Tag = "YearASC" };
    yearProjections.UpdateGroupedByFirstLetter(m => m.Album, m => "A", yearState.LibraryViews.AlbumPageSource);
    await Task.Yield();
    var yearAlbumCards = ((IEnumerable<MusicGroup>)yearState.LibraryViews.AlbumPageSource.Source!).SelectMany(g => g).Select(m => m.Album);
    Check(string.Join(",", yearAlbumCards) == "C album,A album,B album,D album",
        "Year ASC must also order grouped album cards and keep unknown years last.");
    await yearProjections.StopAsync();
    Console.WriteLine("PASS: year sorting keeps album track order and defers unknown years to the end.");

    await FolderNavigationRegression.RunAsync();

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
