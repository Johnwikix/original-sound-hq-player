using SQLite;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services.LastFm;

internal static class HistoryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var database = new SQLiteAsyncConnection(Path.Combine(folder, "history.db"));
        await database.CreateTableAsync<PlaybackHistory>();
        var yesterday = DateTime.UtcNow.AddDays(-1);
        PlaybackHistory Play(string title, DateTime at) => new()
        {
            MusicId = 1, Title = title, Author = "Artist", Album = "Album", TotalDurationMs = 180000,
            DurationPlayedMs = 120000, StartedAt = at, EndedAt = at.AddMinutes(2)
        };
        var first = Play("Before Last.fm", yesterday);
        var existing = Play("Already online", yesterday.AddMinutes(3));
        var repeat = Play("Already online", yesterday.AddMinutes(6));
        var rows = new[]
        {
            first, existing, repeat, Play("Too old", DateTime.UtcNow.AddDays(-20)),
            Play("Seek jump", yesterday.AddMinutes(9)), Play("Unknown", yesterday.AddMinutes(12)),
            Play("Too short", yesterday.AddMinutes(15)), Play("Not enough listening", yesterday.AddMinutes(18))
        };
        rows[4].EndedAt = rows[4].StartedAt.AddSeconds(5);
        rows[5].Author = "Unknown Artist";
        rows[6].TotalDurationMs = 30000;
        rows[7].DurationPlayedMs = 1000;
        await database.InsertAllAsync(rows);
        var source = new SqliteLastFmHistorySource(() => database, new HashSet<string> { "Unknown Artist" });
        var store = new LastFmStore(new TestVault(), Path.Combine(folder, "LastFm.json"));
        await File.WriteAllTextAsync(Path.Combine(folder, "LastFm.json"), "{\"Version\":1,\"Enabled\":false,\"HasCredentials\":false,\"Pending\":[]}");
        check((await store.LoadAsync(default)).Known.Count == 0, "Existing queue files without import bookkeeping stopped loading.");
        await using var server = new ApiServer { ScrobbleFailure = 503 };
        server.DurableProbe = timestamp => StoreChecks.ReadState(store).Contains(timestamp);
        server.RecentNowPlaying = new(first.Title, first.Author, new DateTimeOffset(first.StartedAt).ToUnixTimeSeconds());
        server.RemoteHistory.Add(new(existing.Title, existing.Author, new DateTimeOffset(existing.StartedAt).ToUnixTimeSeconds() + 2));
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await service.StartAsync();
            await service.ConfigureAsync(new string('a', 32), new string('b', 32));
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.RetryAsync();
            check(service.Status.PendingCount == 0 && server.RecentRequests == 0,
                "Startup, authorization or queue retry silently imported local history.");
            server.RecentFailure = 503;
            try { await service.ImportHistoryAsync(source); throw new Exception("Incomplete remote comparison was accepted."); }
            catch (LastFmApiException) { check(service.Status.PendingCount == 0, "Failed comparison partially modified the queue."); }
            server.RecentFailure = 0;
            server.MalformedRecent = true;
            try { await service.ImportHistoryAsync(source); throw new Exception("Malformed history was treated as an empty account."); }
            catch (KeyNotFoundException) { check(service.Status.PendingCount == 0, "Malformed remote history partially changed the queue."); }
            server.MalformedRecent = false;
            server.SingleRecent = true;
            var single = await api.RecentPlaysAsync((await store.ReadIdentityAsync(default))!,
                DateTimeOffset.UtcNow.AddDays(-14).ToUnixTimeSeconds(), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, default);
            check(single.Plays.Count == 1 && single.Plays[0].Title == existing.Title, "Single-object history response was not parsed.");
            server.SingleRecent = false;
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport == new LastFmHistoryImportResult(2, 1, 1, 4, 0), "Wrong history import eligibility or duplicate counts.");
            var saved = await store.LoadAsync(default);
            check(saved.Pending.Count == 2 && saved.Pending[0].StartedAt == new DateTimeOffset(first.StartedAt).ToUnixTimeSeconds(),
                "Existing local history did not enter the durable queue with its original UTC time.");
            check(saved.Pending[1].Track.Title == repeat.Title, "One remote play suppressed a separate repeat listen.");
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport == new LastFmHistoryImportResult(0, 3, 1, 4, 0) && service.Status.PendingCount == 2,
                "Clicking import twice duplicated pending plays.");
            await service.StopAsync();
            service = new(api, store);
            await service.StartAsync();
            await service.ImportHistoryAsync(source);
            check(service.Status.PendingCount == 2 && service.Status.HistoryImport?.Queued == 0, "Restart lost import deduplication.");
            server.ScrobbleFailure = 0;
            await service.RetryAsync();
            await WaitFor(() => service.Status.PendingCount == 0);
            check(server.Accepted.Count == 2 && server.DurableBeforeSend, "Imported plays were not submitted through the durable queue.");
            check((await store.ReadKnownAsync("alice", new string('a', 32), default)).Count == 2, "Successful uploads lost their local acknowledgement markers.");
            await service.StopAsync();
            service = new(api, store);
            await service.StartAsync();
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport?.Queued == 0 && server.Accepted.Count == 2,
                "Already uploaded history was submitted again after restart.");

            var next = Play("Next import", yesterday.AddHours(2));
            await database.InsertAsync(next);
            using (var blocked = new StoreWriteBlocker(store))
            {
                try { await service.ImportHistoryAsync(source); throw new Exception("Failed import commit succeeded."); }
                catch (SQLiteException) { check(service.Status.PendingCount == 0, "Failed import commit acknowledged an unsaved queue."); }
            }

            server.HoldRecent = true;
            server.ScrobbleFailure = 503;
            using (var cancel = new CancellationTokenSource())
            {
                var importing = service.ImportHistoryAsync(source, cancel.Token);
                await server.RecentEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                check(await service.EnqueueAsync(new("Live during import", "Artist", "", 180), DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    Guid.NewGuid(), service.Status.Revision).WaitAsync(TimeSpan.FromSeconds(2)),
                    "A slow history comparison blocked durable recording of a new live play.");
                cancel.Cancel();
                try { await importing.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Canceled import completed."); }
                catch (OperationCanceledException) { check(service.Status.PendingCount == 1, "Cancellation changed the queue beyond the concurrent live play."); }
            }
            server.HoldRecent = false;
            server.ReleaseRecent.TrySetResult();
            server.ScrobbleFailure = 0;
            await service.RetryAsync();
            await WaitFor(() => service.Status.PendingCount == 0);
            check(await database.Table<PlaybackHistory>().CountAsync() == 9
                && (await database.FindAsync<PlaybackHistory>(first.Id)).DurationPlayedMs == first.DurationPlayedMs,
                "Import changed local playback statistics.");

            // Both SQLite's 500-row pages and Last.fm's 200-result pages are exercised.
            var bulk = Enumerable.Range(0, 501).Select(i => Play("Page " + i, yesterday.AddSeconds(i))).ToList();
            await database.InsertAllAsync(bulk);
            foreach (var row in bulk) server.RemoteHistory.Add(new(row.Title, row.Author, new DateTimeOffset(row.StartedAt).ToUnixTimeSeconds()));
            int before = server.RecentRequests;
            server.ScrobbleFailure = 503;
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport?.Queued == 1 && service.Status.HistoryImport?.AlreadyPresent == 504
                && server.RecentRequests - before == 3, "History reconciliation skipped a database/API page.");

            await service.SetEnabledAsync(false);
            try { await service.ImportHistoryAsync(source); throw new Exception("Disabled integration imported history."); }
            catch (InvalidOperationException) { check(service.Status.PendingCount == 1, "Disabled import modified pending plays."); }
            server.UserName = "bob";
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            server.RemoteHistory.Clear();
            server.Accepted.Clear();
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport?.Queued == 505 && service.Status.PendingCount == 505,
                "Another account inherited the original account's deduplication markers.");
            await service.StopAsync();
            // Saturated queues retain existing plays and report the remaining local records.
            var saturated = await store.LoadAsync(default);
            var pending = new List<LastFmScrobble>(saturated.Pending);
            while (pending.Count < 9999)
                pending.Add(new(Guid.NewGuid(), new string('a', 32), "bob", new("Unrelated " + pending.Count, "Artist", "", 180), DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            await store.SaveAsync(saturated with { Pending = pending }, default);
            var additional = new[] { Play("Capacity A", yesterday.AddHours(5)), Play("Capacity B", yesterday.AddHours(6)) };
            await database.InsertAllAsync(additional);
            service = new(api, store);
            await service.StartAsync();
            await service.ImportHistoryAsync(source);
            check(service.Status.PendingCount == 10000 && service.Status.HistoryImport?.Queued == 1
                && service.Status.HistoryImport?.Deferred == 1, "Full queue dropped existing plays or hid deferred history.");
        }
        finally
        {
            server.ReleaseRecent.TrySetResult();
            await service.StopAsync();
            await database.CloseAsync();
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Expected history submission state did not arrive.");
    }
}
