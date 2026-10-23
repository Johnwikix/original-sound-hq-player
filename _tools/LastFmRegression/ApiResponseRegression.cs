using System.Net;
using WinUIMusicPlayer.Services.LastFm;

internal static class ApiResponseRegression
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (string accepted in new[] { "1", "\"1\"" })
            await VerifyAsync(new("accepted " + accepted, Scrobble(accepted, null), LastFmProblem.None, true, true), check);

        foreach (string accepted in new[] { "0", "\"0\"" })
        {
            for (int code = 1; code <= 5; code++)
            {
                string number = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
                foreach (string ignored in new[] { number, "\"" + number + "\"" })
                {
                    bool quota = code == 5;
                    await VerifyAsync(new("ignored " + ignored + " with accepted " + accepted,
                        Scrobble(accepted, ignored), quota ? LastFmProblem.Network : LastFmProblem.Rejected,
                        !quota, false), check);
                }
            }
        }

        foreach (int code in new[] { 8, 9, 29 })
        {
            string number = code.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (string error in new[] { number, "\"" + number + "\"" })
                await VerifyAsync(new("API error " + error, "{\"error\":" + error + "}",
                    code == 9 ? LastFmProblem.SessionExpired : LastFmProblem.Network,
                    false, false, code != 9), check);
        }

        foreach (string invalid in new[] { "null", "true", "{}", "[]", "\"\"", "\"invalid\"", "-1", "\"-1\"", "2", "\"2\"", "1.5", "2147483648" })
            await VerifyAsync(new("invalid accepted " + invalid, Scrobble(invalid, "\"1\""), LastFmProblem.Network, false, false), check);

        foreach (string accepted in new[] { "0", "\"0\"" })
            foreach (string invalid in new[] { "null", "true", "{}", "[]", "\"\"", "\"invalid\"", "-1", "\"-1\"", "0", "\"0\"", "6", "\"6\"", "1.5", "2147483648" })
                await VerifyAsync(new("invalid ignored code " + invalid + " with accepted " + accepted,
                    Scrobble(accepted, invalid), LastFmProblem.Network, false, false), check);

        string[] malformed =
        [
            "{broken",
            "[]",
            "{}",
            "{\"scrobbles\":null}",
            "{\"scrobbles\":{\"@attr\":{}}}",
            "{\"scrobbles\":{\"@attr\":[],\"scrobble\":{}}}",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0}}}",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":[]}}",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":{}}}",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":{\"ignoredMessage\":null}}}",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":{\"ignoredMessage\":{}}}}",
            "{\"error\":null}",
            "{\"error\":\"invalid\"}"
        ];
        for (int i = 0; i < malformed.Length; i++)
            await VerifyAsync(new("malformed response " + i, malformed[i], LastFmProblem.Network, false, false), check);

        await VerifyAsync(new("single-item scrobble array",
            "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":[{\"ignoredMessage\":{\"code\":1}}]}}",
            LastFmProblem.Rejected, true, false), check);
    }

    private static string Scrobble(string accepted, string? ignored) =>
        "{\"scrobbles\":{\"@attr\":{\"accepted\":" + accepted + "}"
        + (ignored is null ? "" : ",\"scrobble\":{\"ignoredMessage\":{\"code\":" + ignored + "}}") + "}}";

    private static async Task VerifyAsync(ResponseCase response, Action<bool, string> check)
    {
        string folder = Path.Combine(AppContext.BaseDirectory, "test-data", "api-responses", Guid.NewGuid().ToString("N"));
        var vault = new Vault();
        await using var store = new LastFmStore(vault, Path.Combine(folder, "LastFm.json"));
        var credentials = new LastFmCredentials(new string('a', 32), new string('b', 32));
        var identity = new LastFmIdentity(credentials, new("synthetic-account", "synthetic-session"));
        long timestamp = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        var play = new LastFmScrobble(Guid.NewGuid(), credentials.ApiKey, identity.Account!.Name,
            new("API response 中文", "Synthetic Artist", "Album", 180), timestamp, new string('c', 64), timestamp);
        await store.SaveIdentityAsync(identity, default);
        await store.SaveAsync(new() { HasCredentials = true, Enabled = true, Pending = [play] }, default);

        // Exercise the real worker, JSON reader and SQLite acknowledgement transaction together.
        // No credentials or requests leave this handler; the only database belongs to this case.
        using var handler = new ResponseHandler(response.Json);
        using var api = new LastFmApiClient(new HttpClient(handler), new Uri("https://lastfm.invalid/"));
        var service = new LastFmService(api, store);
        var finished = new TaskCompletionSource<LastFmStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Changed += status =>
        {
            if (status.IsReady && (status.PendingCount == 0 || status.Problem != LastFmProblem.None))
                finished.TrySetResult(status);
        };
        LastFmStatus status;
        try
        {
            await service.StartAsync();
            status = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { await service.StopAsync(); }

        // Reopen after shutdown so assertions cover persisted state, including accepted mappings.
        var saved = await store.LoadAsync(default);
        string context = "Last.fm " + response.Name + ": ";
        check(status.Problem == response.Problem,
            context + "expected " + response.Problem + ", received " + status.Problem + ".");
        check(status.IsReady && status.Enabled && status.IsAuthenticated == response.Authenticated,
            context + "response parsing changed readiness, enabled state or account authentication.");
        check(status.PendingCount == (response.Dequeued ? 0 : 1)
            && saved.Pending.Count == (response.Dequeued ? 0 : 1),
            context + "queue acknowledgement was lost or an unconfirmed play was removed.");
        check(response.Dequeued || (saved.Pending.Count == 1 && saved.Pending[0] == play),
            context + "retryable response changed the original durable play.");
        check(await store.CountHistoryAsync(true, default) == (response.Accepted ? 1 : 0),
            context + "SQLite accepted history differs from the server acknowledgement.");
        check(await store.CountHistoryAsync(false, default) == (!response.Dequeued || response.Accepted ? 1 : 0),
            context + "SQLite discarded an unresolved reservation or retained a rejected one.");
        check(handler.Requests == 1, context + "one response caused duplicate HTTP submissions.");
    }

    private sealed record ResponseCase(string Name, string Json, LastFmProblem Problem,
        bool Dequeued, bool Accepted, bool Authenticated = true);

    private sealed class ResponseHandler(string json) : HttpMessageHandler
    {
        private int _requests;
        internal int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
        }
    }

    private sealed class Vault : ILastFmCredentialVault
    {
        private string? _value;
        public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(_value);
        public Task WriteAsync(string? value, CancellationToken token)
        {
            _value = value;
            return Task.CompletedTask;
        }
    }
}
