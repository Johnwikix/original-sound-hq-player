using SQLite;
using WinUIMusicPlayer.Model.Stats;
using WinUIMusicPlayer.Services.LastFm;

internal static class OldHistoryRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var midnight = new DateTimeOffset(2026, 10, 23, 0, 0, 0, TimeSpan.FromHours(8));
        var day = new LastFmImportDay(midnight.AddSeconds(2));
        day.Reserve(midnight.AddSeconds(1).ToUnixTimeSeconds());
        check(day.TryAllocate(out long a) && a == midnight.ToUnixTimeSeconds()
            && day.TryAllocate(out long b) && b == midnight.AddSeconds(2).ToUnixTimeSeconds()
            && !day.TryAllocate(out _), "Import-day allocation reused a second, crossed midnight or assigned a future time.");
        var dst = new LastFmImportDay(new DateTimeOffset(2026, 3, 8, 5, 0, 0, TimeSpan.FromHours(-4)),
            TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time"));
        check(dst.TryAllocate(out long dstTime) && dstTime == new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-5)).ToUnixTimeSeconds(),
            "Import day started outside the user's local day at a DST transition.");

        string folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "LastFm.json");
        var database = new SQLiteAsyncConnection(Path.Combine(folder, "history.db"));
        await database.CreateTableAsync<PlaybackHistory>();
        PlaybackHistory Play(string title, int days, int minute = 0) => new()
        {
            MusicId = 1, Title = title, Author = "Artist", Album = "Album", TotalDurationMs = 180000, DurationPlayedMs = 120000,
            StartedAt = DateTime.UtcNow.AddDays(-days).AddMinutes(minute), EndedAt = DateTime.UtcNow.AddDays(-days).AddMinutes(minute + 2)
        };
        var rows = new[] { Play("Old repeat", 20), Play("Old repeat", 20, 3), Play("Old already online", 30),
            Play("Recent", 1), Play("Unqualified", 40), Play("Unknown", 50) };
        rows[4].DurationPlayedMs = 1000;
        rows[5].Author = "Unknown Artist";
        await database.InsertAllAsync(rows);
        var source = new SqliteLastFmHistorySource(() => database, new HashSet<string> { "Unknown Artist" });
        await using var server = new ApiServer { ScrobbleFailure = 503 };
        server.RemoteHistory.Add(new(rows[2].Title, rows[2].Author, new DateTimeOffset(rows[2].StartedAt).ToUnixTimeSeconds()));
        server.DurableProbe = stamp => File.ReadAllText(path).Contains(stamp);
        var store = new LastFmStore(new TestVault(), path);
        using var api = new LastFmApiClient(new HttpClient(), server.Endpoint);
        var service = new LastFmService(api, store);
        try
        {
            await service.StartAsync();
            await service.ConfigureAsync(new string('a', 32), new string('b', 32));
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.ImportHistoryAsync(source);
            check(service.Status.HistoryImport == new LastFmHistoryImportResult(1, 0, 5, 0, 0),
                "Default import uploaded records older than 14 days.");

            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                try { await service.ImportHistoryAsync(source, true); throw new Exception("Failed old-history import commit was acknowledged."); }
                catch (UnauthorizedAccessException)
                {
                    var failed = await store.LoadAsync(default);
                    check(failed.Pending.Count == 1 && failed.HistoryReceipts.Count == 0,
                        "Failed commit left source deduplication markers for unqueued plays.");
                }
            }
            finally { File.SetAttributes(path, FileAttributes.Normal); }
            DateTime before = DateTime.Now.Date;
            await service.ImportHistoryAsync(source, true);
            check(service.Status.HistoryImport == new LastFmHistoryImportResult(2, 2, 0, 2, 0, 2),
                "Opt-in old-history import lost original-date duplicates, repeated listens or eligibility counts.");
            var saved = await store.LoadAsync(default);
            var old = saved.Pending.Where(play => play.Track.Title == "Old repeat").ToList();
            check(old.Count == 2 && old[0].StartedAt != old[1].StartedAt
                && old.All(play => DateTimeOffset.FromUnixTimeSeconds(play.StartedAt).LocalDateTime.Date >= before
                    && DateTimeOffset.FromUnixTimeSeconds(play.StartedAt).LocalDateTime.Date <= DateTime.Now.Date
                    && play.StartedAt <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                "Old repeated plays did not receive distinct, elapsed times on the import day.");
            check(old.All(play => play.HistoryKey?.Length == 64 && play.OriginalStartedAt < LastFmHistory.OldestTimestamp)
                && saved.Pending.Single(play => play.Track.Title == "Recent").StartedAt == new DateTimeOffset(rows[3].StartedAt).ToUnixTimeSeconds(),
                "Retiming changed a recent timestamp or lost the old source identity/time.");
            var readBack = await database.Table<PlaybackHistory>().OrderBy(row => row.Id).ToListAsync();
            check(readBack.Count == rows.Length && readBack.Zip(rows).All(pair => pair.First.StartedAt.Ticks == pair.Second.StartedAt.Ticks
                && pair.First.EndedAt.Ticks == pair.Second.EndedAt.Ticks && pair.First.DurationPlayedMs == pair.Second.DurationPlayedMs),
                "Retiming modified the original local timestamps or statistics.");
            await service.ImportHistoryAsync(source, true);
            check(service.Status.PendingCount == 3 && service.Status.HistoryImport?.Queued == 0,
                "Repeated opt-in import duplicated offline records.");
            await service.StopAsync();
            service = new(api, store);
            await service.StartAsync();
            await service.ImportHistoryAsync(source, true);
            var restored = await store.LoadAsync(default);
            check(restored.Pending.Select(play => (play.HistoryKey, play.StartedAt)).SequenceEqual(saved.Pending.Select(play => (play.HistoryKey, play.StartedAt))),
                "Restart reassigned timestamps or duplicated pending old records.");
            server.ScrobbleFailure = 0;
            await service.RetryAsync();
            await WaitFor(() => service.Status.PendingCount == 0);
            check(server.Accepted.Count == 3 && server.DurableBeforeSend && (await store.LoadAsync(default)).HistoryReceipts.Count == 4,
                "Successful uploads did not durably retain per-source deduplication receipts.");

            // Simulate recent caches expiring and remote history being unavailable for duplicate matching.
            await service.StopAsync();
            await store.SaveAsync((await store.LoadAsync(default)) with { Known = [] }, default);
            server.RemoteHistory.Clear();
            server.Accepted.Clear();
            rows[0].Title = "Metadata corrected later";
            await database.UpdateAsync(rows[0]);
            int requests = server.RecentRequests;
            service = new(api, store);
            await service.StartAsync();
            await service.ImportHistoryAsync(source, true);
            check(service.Status.HistoryImport?.Queued == 0 && service.Status.HistoryImport?.AlreadyPresent == 4
                && server.RecentRequests == requests, "Expired caches or metadata edits lost permanent source deduplication.");
            await service.DisconnectAsync();
            await service.ConfigureAsync(new string('c', 32), new string('b', 32));
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.ImportHistoryAsync(source, true);
            check(service.Status.HistoryImport?.Queued == 0 && server.Accepted.Count == 0,
                "Reconnecting the same account with different app credentials duplicated old history.");

            server.UserName = "bob";
            server.ScrobbleFailure = 503;
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await service.ImportHistoryAsync(source, true);
            check(service.Status.PendingCount == 4 && service.Status.HistoryImport?.Retimed == 3,
                "One account's permanent receipts suppressed another account's history.");
            server.ScrobbleFailure = 0;
            server.BeforeScrobbleResponse = () => File.SetAttributes(path, FileAttributes.ReadOnly);
            await service.RetryAsync();
            await WaitFor(() => service.Status.Problem == LastFmProblem.Storage);
            check(server.Accepted.Count == 1 && (await store.LoadAsync(default)).HistoryReceipts.Count == 4,
                "Failed acknowledgement cleanup partially committed a history receipt.");
            server.BeforeScrobbleResponse = null;
            File.SetAttributes(path, FileAttributes.Normal);
            await service.RetryAsync();
            await WaitFor(() => service.Status.PendingCount == 0);
            check(server.Accepted.Count == 4 && (await store.LoadAsync(default)).HistoryReceipts.Count == 8,
                "Acknowledgement cleanup resent an accepted play or lost an account's receipts.");
        }
        finally
        {
            server.BeforeScrobbleResponse = null;
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            await service.StopAsync();
            await database.CloseAsync();
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
        throw new TimeoutException("Expected old-history upload state did not arrive.");
    }
}
