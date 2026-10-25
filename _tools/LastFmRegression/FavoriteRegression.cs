using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SQLite;
using WinUIMusicPlayer.Services.LastFm;
using WinUIMusicPlayer.Services;

internal static class FavoriteRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "LastFmFavoriteRegression", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = new SQLiteAsyncConnection(Path.Combine(directory, "library.db"));
        await db.ExecuteAsync("CREATE TABLE Music (Id INTEGER PRIMARY KEY, Title TEXT, Author TEXT, IsFavorite INTEGER, [Order] INTEGER)");
        await db.ExecuteAsync("INSERT INTO Music VALUES (1, 'Song & 中文', 'Artist + A', 0, 0)");
        var favorites = new LastFmFavoriteStore(() => db, new(StringComparer.OrdinalIgnoreCase) { "Unknown artist" });
        var vault = new Vault();
        var credentials = new LastFmCredentials(new('a', 32), new('b', 32));
        vault.Identity = new(credentials, new("alice", "alice-session"));
        using var handler = new Server();
        using var api = new LastFmApiClient(new HttpClient(handler), new Uri("https://lastfm.invalid/"));
        LastFmService? service = null;
        async Task<LastFmService> Start(bool first = false)
        {
            var store = new LastFmStore(vault, Path.Combine(directory, "state.json"));
            if (first) await store.SaveAsync(new() { HasCredentials = true, Enabled = true }, default);
            var result = new LastFmService(api, store, new(() => db));
            await result.StartAsync();
            return result;
        }
        var track = new LastFmTrack("Song & 中文", "Artist + A", "", 0);
        LastFmFavoriteChange Change(bool loved) => new(1, track, loved, loved ? 7 : 0);
        try
        {
            var lifecycle = new AppLifecycle();
            var tasks = new ApplicationTasks(lifecycle);
            var finishWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bool started = false;
            var writing = tasks.RunCommittedAsync(async () => { started = true; await finishWrite.Task; });
            check(started, "Accepted favorite writes must start before returning to another UI click.");
            lifecycle.TryBeginExit(out _);
            var drain = tasks.DrainCommittedAsync();
            check(!drain.IsCompleted, "Shutdown did not wait for an accepted local write.");
            bool lateRan = false;
            await tasks.RunCommittedAsync(() => { lateRan = true; return Task.CompletedTask; });
            check(!lateRan, "Shutdown accepted a new favorite operation.");
            finishWrite.SetResult();
            await Task.WhenAll(writing, drain);
            await tasks.DrainAsync();
            service = await Start(first: true);
            handler.Offline = true;
            await service.SaveFavoriteAsync(Change(true));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            check(service.Status.FavoritePendingCount == 1, "Offline favorite intent was lost.");
            check(await db.ExecuteScalarAsync<int>("SELECT IsFavorite FROM Music WHERE Id = 1") == 1, "Offline local favorite was not committed.");
            check(await db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 1") == 7, "Favorite persistence changed local order.");
            await service.SaveFavoriteAsync(Change(false));
            await service.SaveFavoriteAsync(Change(true));
            check(service.Status.FavoritePendingCount == 1, "Rapid toggles did not coalesce by artist/title.");
            await service.StopAsync();
            await db.CloseAsync();
            db = new SQLiteAsyncConnection(Path.Combine(directory, "library.db"));
            service = await Start();
            check(service.Status.FavoritePendingCount == 1, "Restart lost the durable favorite outbox.");
            handler.Offline = false;
            await service.RetryAsync();
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Loved("alice-session", track.Title), "Offline/restart replay did not deliver latest love.");
            check(handler.Writes.Count == 1, "Offline retries produced extra successful writes.");
            check(!handler.SignatureError, "Love/unlove or confirmation signatures/encoding were wrong.");

            await service.SetEnabledAsync(false);
            await favorites.SaveLocalAsync(Change(false), credentials.ApiKey, "alice");
            check(service.Status.FavoritePendingCount == 0, "Recovery fixture did not leave a stale volatile count.");
            await service.RetryAsync();
            check(service.Status.FavoritePendingCount == 1, "Retry did not discover committed work absent from the cached count.");
            await service.SetEnabledAsync(true);
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(!handler.Loved("alice-session", track.Title), "Recovered durable intent was never sent.");

            // Server applies love, then the response disappears. Reopening must reconcile the
            // uncertain request, rather than create another love date by blindly repeating it.
            handler.LoseNextResponse = true;
            var second = track with { Title = "Lost response" };
            await db.ExecuteAsync("INSERT INTO Music VALUES (2, ?, ?, 0, 0)", second.Title, second.Artist);
            await service.SaveFavoriteAsync(new(2, second, true, 8));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            int writes = handler.Writes.Count;
            await service.StopAsync();
            service = await Start();
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Writes.Count == writes && handler.Confirmations > 0, "Restart repeated a remotely applied uncertain love.");

            // The old HTTP response arrives after two new local clicks. Its acknowledgement must
            // not delete the new version. Run this for both final love and final unlove.
            foreach (bool final in new[] { false, true })
            {
                handler.HoldNext();
                await service.SaveFavoriteAsync(Change(!final));
                await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await service.SaveFavoriteAsync(Change(final));
                await service.SaveFavoriteAsync(Change(!final));
                await service.SaveFavoriteAsync(Change(final));
                check(service.Status.FavoritePendingCount == 1, "In-flight changes expanded the same-song queue.");
                handler.Release.TrySetResult();
                await Until(() => service.Status.FavoritePendingCount == 0);
                check(handler.Loved("alice-session", track.Title) == final, "A stale acknowledgement deleted a newer desired state.");
                check(await db.ExecuteScalarAsync<int>("SELECT IsFavorite FROM Music WHERE Id = 1") == (final ? 1 : 0), "Rapid toggles persisted a mutable stale snapshot.");
            }

            handler.HoldNext();
            writes = handler.Writes.Count;
            await service.SaveFavoriteAsync(Change(true));
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var originalIntent = await favorites.PeekAsync(credentials.ApiKey, "alice");
            await service.SaveFavoriteAsync(Change(true));
            var repeatedIntent = await favorites.PeekAsync(credentials.ApiKey, "alice");
            check(originalIntent?.Version == repeatedIntent?.Version && repeatedIntent?.Attempted == true,
                "Repeated identical input reset an in-flight attempt/version.");
            handler.Release.TrySetResult();
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Writes.Count == writes + 1, "Repeated identical favorite input produced duplicate HTTP writes.");

            handler.NumericLoved = true;
            handler.LoseNextResponse = true;
            await service.SaveFavoriteAsync(Change(false));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            writes = handler.Writes.Count;
            handler.MalformedInfo = true;
            var malformedHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void ConfirmationFailed(LastFmStatus status)
            {
                if (handler.MalformedReplies > 0 && status.Problem == LastFmProblem.Network) malformedHandled.TrySetResult();
            }
            service.Changed += ConfirmationFailed;
            await service.RetryAsync();
            await malformedHandled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            service.Changed -= ConfirmationFailed;
            check(service.Status.FavoritePendingCount == 1, "Malformed delivery confirmation discarded an uncertain unlove.");
            handler.MalformedInfo = false;
            await service.RetryAsync();
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Writes.Count == writes && !handler.Loved("alice-session", track.Title),
                "Lost unlove response/numeric loved state caused a repeated write or lost cancellation.");

            await service.SetEnabledAsync(false);
            writes = handler.Writes.Count;
            await service.SaveFavoriteAsync(Change(false));
            await Task.Delay(60);
            check(handler.Writes.Count == writes && service.Status.FavoritePendingCount == 1, "Disabled reporting sent HTTP or discarded local intent.");
            await service.StopAsync();
            service = await Start();
            check(!service.Status.Enabled && service.Status.FavoritePendingCount == 1, "Disabled restart lost or sent the queue.");
            await service.SetEnabledAsync(true);
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(!handler.Loved("alice-session", track.Title), "Re-enabled unlove did not converge.");

            // Persist a session-expired queue, then reauthorize the same account without unlinking.
            handler.ErrorCode = 9;
            await service.SaveFavoriteAsync(Change(true));
            await Until(() => service.Status.Problem == LastFmProblem.SessionExpired);
            check(service.Status.FavoritePendingCount == 1 && !service.Status.IsAuthenticated, "Session expiry dropped favorite intents.");
            handler.ErrorCode = 0;
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Loved("alice-session", track.Title), "Same-account reauthorization lost a pending love.");

            // Switch accounts directly (without explicit unlink). No Alice operation may reach Bob.
            handler.Offline = true;
            await service.SaveFavoriteAsync(Change(false));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            handler.Offline = false;
            handler.Account = "bob";
            await service.BeginAuthorizationAsync();
            handler.HoldNextSession();
            var switching = service.CompleteAuthorizationAsync();
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var oldAccountClick = service.SaveFavoriteAsync(Change(true));
            handler.Release.TrySetResult();
            await Task.WhenAll(switching, oldAccountClick);
            check(service.Status.UserName == "bob" && service.Status.FavoritePendingCount == 0, "Account switch retained another account's outbox.");
            check(!handler.Writes.Any(write => write.Session == "bob-session"), "Alice's pending intent migrated to Bob.");
            await service.SaveFavoriteAsync(Change(true));
            await Until(() => service.Status.FavoritePendingCount == 0);
            check(handler.Loved("bob-session", track.Title), "New account could not report its own favorite.");

            handler.Offline = true;
            await service.SaveFavoriteAsync(Change(false));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            await service.DisconnectAsync();
            check(service.Status.FavoritePendingCount == 0, "Unlink failed to clear the favorite outbox.");
            await service.StopAsync();
            service = await Start();
            check(service.Status.FavoritePendingCount == 0 && !service.Status.IsAuthenticated, "Restart resurrected unlinked intents.");
            handler.Offline = false;
            await service.BeginAuthorizationAsync();
            await service.CompleteAuthorizationAsync();
            check(service.Status.FavoritePendingCount == 0, "Reconnection implicitly imported existing favorites.");

            // Manual import pages the actual database and leaves local membership/order untouched.
            // Pause the worker with an offline error while retaining an authenticated account.
            handler.Offline = true;
            await service.SaveFavoriteAsync(Change(false));
            await Until(() => service.Status.Problem == LastFmProblem.Network);
            await db.RunInTransactionAsync(tx =>
            {
                for (int i = 3; i <= 1030; i++) tx.Execute("INSERT INTO Music VALUES (?, ?, 'Artist', 1, ?)", i, "Import " + i, i);
                tx.Execute("INSERT INTO Music VALUES (1031, 'Unknown', 'Unknown artist', 1, 1031)");
                tx.Execute("INSERT INTO Music VALUES (1032, '', 'Artist', 1, 1032)");
            });
            // Use the production unknown-artist filter in the standalone store too.
            var imported = await favorites.ImportAsync(credentials.ApiKey, "bob");
            check(imported.Result.Queued == 1029 && imported.Result.Ineligible == 2, "Paged manual import skipped favorites or accepted invalid metadata.");
            var repeated = await favorites.ImportAsync(credentials.ApiKey, "bob");
            check(repeated.Result.Queued == 0 && repeated.Pending == imported.Pending, "Repeated manual import duplicated queued songs.");
            check(await db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 1030") == 1030, "Manual reporting reordered local favorites.");
            var firstPending = await favorites.PeekAsync(credentials.ApiKey, "bob");
            check(firstPending is not null, "Manual import did not persist its outbox.");
            // Conditional acknowledgement of a replaced entry cannot delete its new version.
            await favorites.SaveLocalAsync(new(2, second, false, 0), credentials.ApiKey, "bob");
            await favorites.CompleteAsync(firstPending!);
            check(await db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox WHERE Loved = 0") >= 1, "Old acknowledgement removed a replacement unlove.");

            // Prove local favorite + outbox atomicity under a real SQLite failure.
            await db.ExecuteAsync("CREATE TRIGGER fail_favorite BEFORE INSERT ON LastFmFavoriteOutbox BEGIN SELECT RAISE(ABORT, 'test'); END");
            bool failed = false;
            try { await favorites.SaveLocalAsync(Change(true), credentials.ApiKey, "bob"); }
            catch (SQLiteException) { failed = true; }
            check(failed && await db.ExecuteScalarAsync<int>("SELECT IsFavorite FROM Music WHERE Id = 1") == 0, "Outbox failure committed only half of a local favorite edit.");
            await db.ExecuteAsync("DROP TRIGGER fail_favorite");
            check(!handler.SignatureError, "Favorite API signature validation failed.");
        }
        finally
        {
            handler.Release.TrySetResult();
            if (service is not null) await service.StopAsync();
            await db.CloseAsync();
        }
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Vault : ILastFmCredentialVault
    {
        private string? _value;
        public LastFmIdentity? Identity { set => _value = JsonSerializer.Serialize(value, LastFmJsonContext.Default.LastFmIdentity); }
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(_value);
        public Task WriteAsync(string? value, CancellationToken token) { _value = value; return Task.CompletedTask; }
    }

    private sealed class Server : HttpMessageHandler
    {
        public volatile bool Offline, LoseNextResponse;
        public volatile int ErrorCode;
        public string Account = "alice";
        public int Confirmations, MalformedReplies;
        public bool SignatureError;
        public bool NumericLoved, MalformedInfo;
        public readonly ConcurrentQueue<(string Session, string Title, bool Loved)> Writes = new();
        private readonly ConcurrentDictionary<string, bool> _remote = new();
        public TaskCompletionSource Entered = NewSignal(), Release = NewSignal();
        private bool _hold, _holdSession;
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void HoldNext() { Entered = NewSignal(); Release = NewSignal(); _hold = true; }
        public void HoldNextSession() { Entered = NewSignal(); Release = NewSignal(); _holdSession = true; }
        public bool Loved(string session, string title) => _remote.TryGetValue(session + ":" + title, out bool loved) && loved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Yield();
            if (Offline) throw new HttpRequestException("Offline test");
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in body.Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                parameters[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
            }
            string signature = parameters["api_sig"];
            parameters.Remove("api_sig"); parameters.Remove("format");
            string signed = string.Concat(parameters.Select(pair => pair.Key + pair.Value)) + new string('b', 32);
            SignatureError |= signature != Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(signed)));
            string method = parameters["method"];
            string json = "{}";
            if (method == "auth.getToken") json = "{\"token\":\"token\"}";
            else if (method == "auth.getSession")
            {
                if (_holdSession) { _holdSession = false; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                json = "{\"session\":{\"name\":\"" + Account + "\",\"key\":\"" + Account + "-session\"}}";
            }
            else if (ErrorCode != 0) json = "{\"error\":" + ErrorCode + "}";
            else if (method == "track.getInfo")
            {
                Interlocked.Increment(ref Confirmations);
                string value = Loved(parameters["username"] + "-session", parameters["track"]) ? "1" : "0";
                json = MalformedInfo ? "{\"track\":[]}" : "{\"track\":{\"userloved\":" + (NumericLoved ? value : "\"" + value + "\"") + "}}";
                if (MalformedInfo) Interlocked.Increment(ref MalformedReplies);
            }
            else if (method is "track.love" or "track.unlove")
            {
                bool loved = method == "track.love";
                string session = parameters["sk"], title = parameters["track"];
                Writes.Enqueue((session, title, loved));
                _remote[session + ":" + title] = loved;
                if (_hold) { _hold = false; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
                if (LoseNextResponse) { LoseNextResponse = false; throw new HttpRequestException("Response lost after remote commit"); }
            }
            else throw new Exception("Unexpected favorite API method: " + method);
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
