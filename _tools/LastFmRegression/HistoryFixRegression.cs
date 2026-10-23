using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using SQLite;
using WinUIMusicPlayer.Services.LastFm;

internal static class HistoryFixRegression
{
    private static readonly string ApiKey = new('a', 32);
    private static readonly string Secret = new('b', 32);
    private static readonly LastFmTrack Track = new("Same song", "Artist", "Album", 180);
    private static string Folder()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static LastFmHistoryEntry Entry(int id, long timestamp) => new(id, Track.Title, Track.Artist, Track.Album,
        180000, 120000, DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime, DateTimeOffset.FromUnixTimeSeconds(timestamp + 120).UtcDateTime);
    private static async Task Connect(LastFmService service)
    {
        await service.StartAsync();
        await service.ConfigureAsync(ApiKey, Secret);
        await service.BeginAuthorizationAsync();
        await service.CompleteAuthorizationAsync();
    }
    private static async Task WaitFor(Func<bool> predicate)
    {
        for (int i = 0; i < 800; i++) { if (predicate()) return; await Task.Delay(10); }
        throw new TimeoutException("History fix regression did not reach the expected state.");
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var havana = TimeZoneInfo.FindSystemTimeZoneById("Cuba Standard Time");
        var firstHour = new LastFmImportDay(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(-4)), havana);
        long firstMidnight = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4)).ToUnixTimeSeconds();
        check(firstHour.TryAllocate(out long at) && at == firstMidnight,
            "Ambiguous midnight chose the later UTC instant and blocked the first hour of imports.");
        var secondHour = new LastFmImportDay(new DateTimeOffset(2026, 11, 1, 0, 30, 0, TimeSpan.FromHours(-5)), havana);
        check(secondHour.TryAllocate(out at) && at == firstMidnight, "Repeated midnight changed the start of the local import day.");
        await SeparateTimeDomains(check);
        await AcknowledgementTransitions(check);
        await ReservationRestart(check);
        await OrphanReservation(check);
        await LegacyMigration(check);
        await NetworkFairness(check);
        await IncrementalLedger(check);
        await CallerThreadWork(check);
    }

    private static async Task SeparateTimeDomains(Action<bool, string> check)
    {
        var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
        long assigned = new DateTimeOffset(DateTime.Now.Date.AddDays(-1)).ToUnixTimeSeconds();
        var old = Entry(1, assigned - 30 * 86400);
        var genuine = Entry(2, assigned + 5);
        var uploaded = new LastFmScrobble(Guid.NewGuid(), ApiKey, "alice", Track, assigned, LastFmHistory.SourceKey(old),
            new DateTimeOffset(old.StartedAt).ToUnixTimeSeconds(), true);
        await store.SaveAsync(new() { Pending = [uploaded] }, default);
        await store.CommitAsync(new(), new(uploaded, true), [], default);
        await using var server = new ApiServer { ScrobbleFailure = 503 };
        server.RemoteHistory.Add(new(Track.Title, Track.Artist, assigned));
        server.DurableProbe = timestamp => StoreChecks.ReadState(store).Contains(timestamp);
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await Connect(service);
            await service.ImportHistoryAsync(new Source(genuine));
            var state = await store.LoadAsync(default);
            check(service.Status.HistoryImport?.Queued == 1 && state.Pending.Single().StartedAt == assigned + 5,
                "A retimed upload swallowed a genuine later play of the same song.");
            check(await store.CountHistoryAsync(true, default) == 1, "A never-uploaded source received a permanent success marker.");
            server.ScrobbleFailure = 0;
            await service.RetryAsync();
            await WaitFor(() => service.Status.PendingCount == 0);
            check(server.Accepted.Count == 1 && await store.CountHistoryAsync(true, default) == 2,
                "The genuine play was not uploaded exactly once.");
        }
        finally { await service.StopAsync(); }
    }

    private static async Task AcknowledgementTransitions(Action<bool, string> check)
    {
        foreach (bool switchAccount in new[] { false, true })
        {
            var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
            await using var server = new ApiServer { ScrobbleFailure = 503 };
            server.DurableProbe = timestamp => StoreChecks.ReadState(store).Contains(timestamp);
            using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
            var service = new LastFmService(api, store);
            StoreWriteBlocker? blocker = null;
            try
            {
                await Connect(service);
                var source = new Source(Entry(1, DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds()));
                await service.ImportHistoryAsync(source, true);
                await WaitFor(() => service.Status.Problem == LastFmProblem.Network);
                server.ScrobbleFailure = 0;
                server.BeforeScrobbleResponse = () => blocker = new StoreWriteBlocker(store);
                await service.RetryAsync();
                await WaitFor(() => service.Status.Problem == LastFmProblem.Storage);
                check(server.Accepted.Count == 1 && await store.CountHistoryAsync(true, default) == 0,
                    "The injected acknowledgement write failure did not occur after HTTP success.");
                server.BeforeScrobbleResponse = null;
                blocker!.Dispose(); blocker = null;
                if (switchAccount)
                {
                    server.UserName = "bob";
                    await service.BeginAuthorizationAsync();
                    await service.CompleteAuthorizationAsync();
                }
                else await service.DisconnectAsync();
                check(await store.CountHistoryAsync(true, default) == 1 && service.Status.PendingCount == 0,
                    "Changing identity discarded an explicit server acknowledgement before persisting its source mapping.");
                server.UserName = "alice";
                await service.BeginAuthorizationAsync();
                await service.CompleteAuthorizationAsync();
                await service.StopAsync();
                service = new(api, store);
                await service.StartAsync();
                await service.ImportHistoryAsync(source, true);
                check(service.Status.HistoryImport?.Queued == 0 && server.Accepted.Count == 1,
                    "A failed local acknowledgement caused a duplicate after identity transition and restart.");
            }
            finally
            {
                server.BeforeScrobbleResponse = null;
                blocker?.Dispose();
                await service.StopAsync();
            }
        }
    }

    private static async Task ReservationRestart(Action<bool, string> check)
    {
        string path = Path.Combine(Folder(), "LastFm.json");
        var vault = new TestVault();
        var store = new LastFmStore(vault, path);
        long assigned = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var old = Entry(1, assigned - 30 * 86400);
        var reserved = new LastFmScrobble(Guid.NewGuid(), ApiKey, "alice", Track, assigned, LastFmHistory.SourceKey(old), assigned - 30 * 86400, true);
        await store.SaveAsync(new() { HasCredentials = true, Pending = [reserved] }, default);
        await store.SaveIdentityAsync(new(new(ApiKey, Secret), new("alice", "test-session")), default);
        await using var server = new ApiServer { ScrobbleFailure = 503 };
        server.DurableProbe = timestamp => StoreChecks.ReadState(store).Contains(timestamp);
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await service.StartAsync();
            await service.DisconnectAsync();
            await service.StopAsync();
            store = new(vault, path);
            service = new(api, store);
            await service.StartAsync();
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.ImportHistoryAsync(new Source(old), true);
            check((await store.LoadAsync(default)).Pending.Single().StartedAt == assigned,
                "Unlink/restart reassigned a prior day's unresolved upload reservation.");
            await service.DisconnectAsync();
            server.RemoteHistory.Add(new(Track.Title, Track.Artist, assigned));
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.ImportHistoryAsync(new Source(old), true);
            check(service.Status.PendingCount == 0 && await store.CountHistoryAsync(true, default) == 1,
                "An unresolved reservation already present remotely was submitted again.");
        }
        finally { await service.StopAsync(); }
    }

    private static async Task OrphanReservation(Action<bool, string> check)
    {
        var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
        var first = Entry(1, DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds());
        long midnight = new DateTimeOffset(DateTime.Now.Date).ToUnixTimeSeconds();
        var reservation = new LastFmScrobble(Guid.NewGuid(), ApiKey, "alice", Track, midnight,
            LastFmHistory.SourceKey(first), new DateTimeOffset(first.StartedAt).ToUnixTimeSeconds(), true);
        await store.SaveAsync(new() { Pending = [reservation] }, default);
        await store.SaveAsync(new(), default); // unlink leaves the durable reservation behind
        await using var server = new ApiServer { ScrobbleFailure = 503 };
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await Connect(service);
            await service.ImportHistoryAsync(new Source(Entry(2, DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeSeconds())), true);
            check((await store.LoadAsync(default)).Pending.Single().StartedAt != midnight,
                "An unlink reservation absent from the current import lost its assigned second to another source.");
        }
        finally { await service.StopAsync(); }
    }

    private static async Task LegacyMigration(Action<bool, string> check)
    {
        string path = Path.Combine(Folder(), "LastFm.json");
        var vault = new TestVault();
        long assigned = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        var genuine = Entry(2, assigned + 5);
        var legacy = new LastFmSavedState
        {
            Version = 1, HasCredentials = true, Enabled = true,
            HistoryReceipts = [new("alice", new string('c', 64))],
            Known = [new(ApiKey, "alice", LastFmHistory.TrackKey(Track.Title, Track.Artist), assigned)]
        };
        string json = JsonSerializer.Serialize(legacy, LastFmJsonContext.Default.LastFmSavedState);
        await File.WriteAllTextAsync(path, json);
        var store = new LastFmStore(vault, path);
        await store.SaveIdentityAsync(new(new(ApiKey, Secret), new("alice", "test-session")), default);
        await using var server = new ApiServer();
        server.RemoteHistory.Add(new(Track.Title, Track.Artist, assigned));
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await service.StartAsync();
            await service.ImportHistoryAsync(new Source(genuine));
            check(service.Status.HistoryImport is { Queued: 0, Deferred: 1, Uncertain: 1 }
                && await store.CountHistoryAsync(true, default) == 1,
                "Legacy ambiguous aliases invented a success receipt or silently uploaded a duplicate.");
            check(await File.ReadAllTextAsync(path) == json, "One-time migration overwrote the original JSON backup.");
            await service.StopAsync();
            await File.WriteAllTextAsync(path, "{broken-after-migration");
            var reopened = new LastFmStore(vault, path);
            check((await reopened.LoadAsync(default)).Version == 2 && await reopened.CountHistoryAsync(true, default) == 1,
                "An initialized database re-imported stale JSON or lost permanent markers.");
            await reopened.CloseAsync();
        }
        finally { await service.StopAsync(); }
    }

    private static async Task NetworkFairness(Action<bool, string> check)
    {
        var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
        await using var server = new ApiServer();
        long timestamp = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
        for (int i = 0; i < 2399; i++) server.RemoteHistory.Add(new("Other " + i, "Artist", timestamp + i));
        server.DurableProbe = value => StoreChecks.ReadState(store).Contains(value);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.RecentPageRequested = _ => entered.TrySetResult();
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await Connect(service);
            var importing = service.ImportHistoryAsync(new Source(Entry(1, timestamp)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var watch = Stopwatch.StartNew();
            await service.NowPlayingAsync(Track, service.Status.Revision, default).WaitAsync(TimeSpan.FromSeconds(2));
            watch.Stop();
            check(!importing.IsCompleted && server.NowPlaying.Count == 1 && watch.Elapsed < TimeSpan.FromSeconds(2),
                "History pagination retained the shared network slot between pages.");
            await service.EnqueueAsync(Track with { Title = "Live" }, timestamp + 6000, Guid.NewGuid(), service.Status.Revision);
            await WaitFor(() => server.Accepted.Count > 0);
            check(!importing.IsCompleted, "The offline worker could not submit while history pages were pacing.");
            await importing;
            await WaitFor(() => service.Status.PendingCount == 0);
            check(server.RecentRequests == 12 && server.Accepted.Count == 2, "Fair interleaving lost a remote page or a local play.");
            Console.WriteLine($"12-page import: Now Playing waited {watch.Elapsed.TotalMilliseconds:F1} ms.");
        }
        finally { await service.StopAsync(); }
    }

    private static async Task IncrementalLedger(Action<bool, string> check)
    {
        var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
        var receipts = new List<LastFmHistoryReceipt>(100000);
        for (int i = 0; i < 100000; i++) receipts.Add(new("alice", i.ToString("x64")));
        await store.CommitAsync(new(), null, receipts, default);
        check(await store.CountHistoryAsync(true, default) == 100000 && StoreChecks.ReadState(store).Length < 1024,
            "Permanent receipts remained embedded in the queue state document.");
        receipts.Clear();
        await store.SaveAsync(new(), default);
        long before = GC.GetTotalAllocatedBytes(true);
        var watch = Stopwatch.StartNew();
        for (int i = 0; i < 5; i++) await store.SaveAsync(new() { Enabled = i % 2 == 0 }, default);
        watch.Stop();
        long allocated = GC.GetTotalAllocatedBytes(true) - before;
        check(allocated < 5 * 1024 * 1024 && await store.CountHistoryAsync(true, default) == 100000,
            "Small state commits allocated in proportion to the lifetime receipt ledger.");
        await store.CloseAsync();
        before = GC.GetTotalAllocatedBytes(true);
        var loaded = await store.LoadAsync(default);
        long startupAllocated = GC.GetTotalAllocatedBytes(true) - before;
        check(loaded.HistoryReceipts.Count == 0 && startupAllocated < 5 * 1024 * 1024,
            "Startup eagerly loaded the permanent receipt ledger.");
        Console.WriteLine($"100,000 receipts: five warm commits allocated {allocated:N0} bytes in {watch.Elapsed.TotalMilliseconds:F1} ms; restart allocated {startupAllocated:N0} bytes.");
        await store.CloseAsync();
    }

    private static async Task CallerThreadWork(Action<bool, string> check)
    {
        var store = new LastFmStore(new TestVault(), Path.Combine(Folder(), "LastFm.json"));
        await store.LoadAsync(default); // Warm the database and semaphore path before measuring.
        var pending = new List<LastFmScrobble>(10000);
        var track = new LastFmTrack(new string('t', 500), new string('a', 500), new string('l', 1000), 180);
        for (int i = 0; i < 10000; i++) pending.Add(new(Guid.NewGuid(), ApiKey, "alice", track, 1700000000 + i));
        var state = new LastFmSavedState { Pending = pending };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var saving = store.SaveAsync(state, default);
        long callerAllocated = GC.GetAllocatedBytesForCurrentThread() - before;
        await saving;
        check(callerAllocated < 65536, "A warm large-queue commit serialized the queue on its caller (the WinUI sampling thread).");
        Console.WriteLine($"10,000 queued plays: synchronous calling thread allocated {callerAllocated:N0} bytes before returning its save task.");
        await store.CloseAsync();
    }

    private sealed class Source(params LastFmHistoryEntry[] rows) : ILastFmHistorySource
    {
        public Task<int> CountOlderAsync(DateTime fromUtc, CancellationToken token) => Task.FromResult(rows.Count(row => row.StartedAt < fromUtc));
        public async IAsyncEnumerable<LastFmHistoryEntry> ReadAsync(DateTime fromUtc, DateTime toUtc, [EnumeratorCancellation] CancellationToken token)
        {
            await Task.Yield();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (row.StartedAt >= fromUtc && row.StartedAt <= toUtc) yield return row;
            }
        }
    }
}
