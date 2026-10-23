using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using SQLite;
using WinUIMusicPlayer.Services.LastFm;

internal static class FavoriteFixRegression
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await FairnessAsync(check, failFavorites: true);
        await FairnessAsync(check, failFavorites: false);
        await OrderAsync(check);
        await BatchesAsync(check);
        await CancelAsync(check);
        await DisconnectAsync(check);
        await StopDuringImportAsync(check);
    }

    private static async Task FairnessAsync(Action<bool, string> check, bool failFavorites)
    {
        await using var f = await Fixture.CreateAsync(2, start: false, pendingPlay: true);
        f.Http.FailFavorites = failFavorites;
        f.Http.FailScrobbles = !failFavorites;
        await f.Favorites.SaveLocalAsync(new(1, Track(1), true, 1), ApiKey, "alice");
        await f.Favorites.SaveLocalAsync(new(2, Track(2), true, 2), ApiKey, "alice");
        await f.Service.StartAsync();
        await Until(() => failFavorites ? f.Http.Scrobbles == 1 && f.Service.Status.PendingCount == 0
            : f.Http.FavoriteWrites >= 2 && f.Service.Status.FavoritePendingCount == 0);
        check(failFavorites ? f.Http.FavoriteAttempts >= 1 : f.Http.ScrobbleAttempts >= 1,
            "Fairness fixture did not exercise an actual failed request/retry wait.");
        check(failFavorites ? f.Service.Status.PendingCount == 0 : f.Service.Status.FavoritePendingCount == 0,
            "A retryable failure starved the other healthy queue.");
        check(failFavorites ? f.Service.Status.FavoritePendingCount > 0 : f.Service.Status.PendingCount == 1,
            "Fairness discarded the failing request instead of keeping it durable.");
    }

    private static async Task OrderAsync(Action<bool, string> check)
    {
        await using var f = await Fixture.CreateAsync(1, enabled: false);
        await f.Db.ExecuteAsync("UPDATE Music SET IsFavorite = 0, [Order] = 0 WHERE Id = 1");
        await f.Service.BeginAuthorizationAsync();
        f.Http.HoldSession = true;
        var authorizing = f.Service.CompleteAuthorizationAsync();
        await f.Http.SessionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var favorite = f.Service.SaveFavoriteAsync(new(1, Track(1), true, 7));
        var order = f.Service.SaveFavoriteOrderAsync([new(1, 1)]);
        check(!favorite.IsCompleted && !order.IsCompleted, "Favorite and drag did not share the authorization-blocked write sequence.");
        f.Http.ReleaseSession.TrySetResult();
        await Task.WhenAll(authorizing, favorite, order);
        check(await f.Db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 1") == 1,
            "An earlier favorite snapshot overwrote the later drag order.");
        await Until(() => f.Service.Status.FavoritePendingCount == 0);
        int writes = f.Http.FavoriteWrites;
        await f.Service.SaveFavoriteOrderAsync([new(1, 8)]);
        check(f.Http.FavoriteWrites == writes && await f.Db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 1") == 8,
            "Sorting reported another love or failed to persist the latest order.");
        await f.Service.SaveFavoriteAsync(new(1, Track(1), false, 0));
        await f.Service.SaveFavoriteOrderAsync([new(1, 99)]);
        check(await f.Db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 1") == 0
            && await f.Db.ExecuteScalarAsync<int>("SELECT IsFavorite FROM Music WHERE Id = 1") == 0,
            "A late drag resurrected or reordered an unfavorited song.");
    }

    private static async Task BatchesAsync(Action<bool, string> check)
    {
        await using var f = await Fixture.CreateAsync(2050);
        // The later file is the same Last.fm track. An edit must protect both files, even if
        // the worker has acknowledged/deleted the unlove before it reaches the later page.
        await f.Db.ExecuteAsync("UPDATE Music SET Title = 'Song 1' WHERE Id = 1200");
        int prior = 0, largestBatch = 0, batches = 0;
        Task? localEdit = null, playback = null, databaseRead = null, addedMusic = null;
        bool interleaved = false;
        f.Service.Changed += status =>
        {
            if (status.FavoriteImport is not { } import || import.Queued <= prior) return;
            largestBatch = Math.Max(largestBatch, import.Queued - prior);
            prior = import.Queued;
            batches++;
            if (localEdit is null)
            {
                localEdit = f.Service.SaveFavoriteAsync(new(1, Track(1), false, 0));
                playback = f.Service.EnqueueAsync(new("Parallel playback", "Artist", "", 180),
                    DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds(), Guid.NewGuid(), status.Revision);
                databaseRead = f.Db.ExecuteScalarAsync<int>("SELECT Id FROM Music WHERE Id = 1");
                addedMusic = f.Db.ExecuteAsync("INSERT INTO Music VALUES (5000, 'New during import', 'Artist', 1, 5000)");
            }
            else interleaved |= localEdit.IsCompletedSuccessfully && playback!.IsCompletedSuccessfully && databaseRead!.IsCompletedSuccessfully;
        };
        await f.Service.ImportFavoritesAsync();
        await Task.WhenAll(localEdit!, playback!, databaseRead!, addedMusic!);
        check(largestBatch <= 512 && batches >= 5, "Import held a full-library transaction instead of committing bounded pages.");
        check(interleaved, "Favorite saving, playback enqueue or library reads could not complete between pages.");
        await Until(() => f.Service.Status.FavoritePendingCount == 0 && f.Service.Status.PendingCount == 0);
        check(!f.Http.Loved("Song 1"), "A later duplicate file overwrote a new mid-import unlove.");
        check(!f.Http.Loved("New during import"), "The import ignored its initial ID bound and included later library additions.");
        check(await f.Db.ExecuteScalarAsync<int>("SELECT [Order] FROM Music WHERE Id = 2050") == 2050,
            "Batched reporting changed local favorite order.");
        var edited = new HashSet<string>(StringComparer.Ordinal) { LastFmHistory.TrackKey("Song 1", "Artist") };
        var first = await f.Favorites.ImportBatchAsync(ApiKey, "alice", 512, 2050, edited);
        await f.Favorites.ImportBatchAsync(ApiKey, "alice", first.LastId, 2050, edited);
        check(await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox WHERE [Key] = ?",
            ApiKey + ":ALICE:" + LastFmHistory.TrackKey("Song 1", "Artist")) == 0,
            "Import protection depended on the unlove outbox row surviving acknowledgement.");
    }

    private static async Task CancelAsync(Action<bool, string> check)
    {
        await using var f = await Fixture.CreateAsync(2050);
        f.Http.FailFavorites = true;
        using var cancel = new CancellationTokenSource();
        f.Service.Changed += status =>
        {
            if (status.FavoriteImport?.Queued >= 512) cancel.Cancel();
        };
        bool canceled = false;
        try { await f.Service.ImportFavoritesAsync(cancel.Token); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && f.Service.Status.FavoriteImport is { Queued: 512, Canceled: true },
            "Cancellation did not stop at a committed page and preserve its result.");
        check(await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox") == 512,
            "Cancellation discarded committed intents or continued adding later pages.");
        check(await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Music WHERE IsFavorite = 1") == 2050,
            "Canceling reporting changed local favorite membership.");
        await f.Service.StopAsync();
        await f.Db.CloseAsync();
        var reopened = new SQLiteAsyncConnection(f.LibraryPath);
        var persisted = new LastFmStore(f.Vault, f.StatePath);
        using var api = new LastFmApiClient(new HttpClient(new Server()), new Uri("https://lastfm.invalid/"));
        var restarted = new LastFmService(api, persisted, new(() => reopened));
        try
        {
            await restarted.StartAsync();
            await Until(() => restarted.Status.FavoritePendingCount == 0);
            check(await reopened.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox") == 0,
                "Restart failed to deliver committed import pages.");
        }
        finally { await restarted.StopAsync(); await reopened.CloseAsync(); }
    }

    private static async Task DisconnectAsync(Action<bool, string> check)
    {
        await using var f = await Fixture.CreateAsync(2050);
        f.Http.FailFavorites = true;
        Task? disconnect = null;
        f.Service.Changed += status =>
        {
            if (status.FavoriteImport?.Queued >= 512 && disconnect is null) disconnect = f.Service.DisconnectAsync();
        };
        await f.Service.ImportFavoritesAsync();
        await disconnect!;
        check(!f.Service.Status.IsAuthenticated && f.Service.Status.FavoritePendingCount == 0,
            "Unlink did not cancel the import before clearing the account queue.");
        check(await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM LastFmFavoriteOutbox") == 0,
            "A later page recreated an unlinked account's intents.");
    }

    private static async Task StopDuringImportAsync(Action<bool, string> check)
    {
        await using var f = await Fixture.CreateAsync(2050);
        f.Http.FailFavorites = true;
        Task? stopping = null;
        f.Service.Changed += status =>
        {
            if (status.FavoriteImport?.Queued >= 512 && stopping is null) stopping = f.Service.StopAsync();
        };
        bool canceled = false;
        try { await f.Service.ImportFavoritesAsync(); }
        catch (OperationCanceledException) { canceled = true; }
        await stopping!;
        check(canceled && await f.Favorites.ReadPendingCountAsync() == 512,
            "Stopping waited for the whole import or discarded its committed page.");
        check(await f.Db.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM Music WHERE IsFavorite = 1") == 2050,
            "Stopping a partial import changed local favorites.");
    }

    private const string ApiKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static LastFmTrack Track(int id) => new("Song " + id, "Artist", "", 180);
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string LibraryPath = "", StatePath = "";
        public SQLiteAsyncConnection Db = null!;
        public Vault Vault = new();
        public Server Http = new();
        public LastFmFavoriteStore Favorites = null!;
        public LastFmService Service = null!;
        private LastFmApiClient _api = null!;
        public static async Task<Fixture> CreateAsync(int songs, bool start = true, bool enabled = true, bool pendingPlay = false)
        {
            string directory = Path.Combine(Path.GetTempPath(), "LastFmFavoriteFix", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var f = new Fixture { LibraryPath = Path.Combine(directory, "library.db"), StatePath = Path.Combine(directory, "state.json") };
            f.Db = new(f.LibraryPath);
            await f.Db.ExecuteAsync("CREATE TABLE Music (Id INTEGER PRIMARY KEY, Title TEXT, Author TEXT, IsFavorite INTEGER, [Order] INTEGER)");
            await f.Db.RunInTransactionAsync(tx =>
            {
                for (int i = 1; i <= songs; i++) tx.Execute("INSERT INTO Music VALUES (?, ?, 'Artist', 1, ?)", i, "Song " + i, i);
            });
            var store = new LastFmStore(f.Vault, f.StatePath);
            var state = new LastFmSavedState { Enabled = enabled, HasCredentials = true };
            if (pendingPlay) state.Pending.Add(new(Guid.NewGuid(), ApiKey, "alice", Track(1), DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds()));
            await store.SaveAsync(state, default);
            f.Favorites = new(() => f.Db);
            f._api = new(new HttpClient(f.Http), new Uri("https://lastfm.invalid/"));
            f.Service = new(f._api, store, f.Favorites, TimeSpan.FromMilliseconds(10));
            if (start) await f.Service.StartAsync();
            return f;
        }
        public async ValueTask DisposeAsync()
        {
            Http.ReleaseSession.TrySetResult();
            try { await Service.StopAsync(); }
            finally { _api.Dispose(); await Db.CloseAsync(); }
        }
    }

    private sealed class Vault : ILastFmCredentialVault
    {
        private string? _value = JsonSerializer.Serialize(new LastFmIdentity(new(ApiKey, new('b', 32)), new("alice", "session")),
            LastFmJsonContext.Default.LastFmIdentity);
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(_value);
        public Task WriteAsync(string? value, CancellationToken token) { _value = value; return Task.CompletedTask; }
    }

    private sealed class Server : HttpMessageHandler
    {
        public bool FailFavorites, FailScrobbles, HoldSession;
        public int FavoriteAttempts, FavoriteWrites, ScrobbleAttempts, Scrobbles;
        private readonly ConcurrentDictionary<string, bool> _loved = new();
        public readonly TaskCompletionSource SessionEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource ReleaseSession = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Loved(string title) => _loved.TryGetValue(title, out bool value) && value;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string body = await request.Content!.ReadAsStringAsync(token);
            await Task.Yield();
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in body.Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                data[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
            }
            string json;
            switch (data["method"])
            {
                case "auth.getToken": json = "{\"token\":\"token\"}"; break;
                case "auth.getSession":
                    if (HoldSession) { SessionEntered.TrySetResult(); await ReleaseSession.Task.WaitAsync(token); }
                    json = "{\"session\":{\"name\":\"alice\",\"key\":\"session\"}}";
                    break;
                case "track.getInfo": json = "{\"track\":{\"userloved\":" + (Loved(data["track"]) ? 1 : 0) + "}}"; break;
                case "track.love":
                case "track.unlove":
                    Interlocked.Increment(ref FavoriteAttempts);
                    json = FailFavorites ? "{\"error\":11}" : "{}";
                    if (!FailFavorites) { _loved[data["track"]] = data["method"] == "track.love"; Interlocked.Increment(ref FavoriteWrites); }
                    break;
                case "track.scrobble":
                    Interlocked.Increment(ref ScrobbleAttempts);
                    json = FailScrobbles ? "{\"error\":11}" : "{\"scrobbles\":{\"@attr\":{\"accepted\":1}}}";
                    if (!FailScrobbles) Interlocked.Increment(ref Scrobbles);
                    break;
                default: throw new Exception("Unexpected API call in favorite fix regression.");
            }
            return new(HttpStatusCode.OK) { Content = new StringContent(json) };
        }
    }
}
