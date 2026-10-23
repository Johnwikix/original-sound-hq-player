using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SQLite;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Services.LastFm;

int checks = 0;
void Check(bool value, string message) { checks++; if (!value) throw new Exception(message); }
static long Stamp(int milliseconds) => (long)(milliseconds / 1000.0 * Stopwatch.Frequency);
static ProgressSnapshot Snapshot(int position, int at, bool playing = true, long epoch = 1, long seek = 0, double rate = 1, int duration = 180000)
    => new(at, epoch, position, duration, Stamp(at), playing, seek, rate);
var song = new LastFmTrack("Test & 中文", "Artist + One", "Album / A", 180);
var session = new LastFmListenSession(song, Snapshot(0, 0), 1700000000);
for (int i = 1; i <= 89; i++) session.Observe(Snapshot(i * 1000, i * 1000));
Check(!session.IsQualified, "A three minute song qualifies before 90 seconds.");
session.Observe(Snapshot(90000, 90000));
Check(session.IsQualified && session.StartedAt == 1700000000, "Qualification must retain playback start time.");

session = new(song, Snapshot(0, 0), 1);
session.Observe(Snapshot(120000, 1000, seek: 1, epoch: 2));
Check(session.ListenedMilliseconds == 0, "Fast forward granted listening credit.");
session.Observe(Snapshot(121000, 2000, seek: 1, epoch: 2));
session.Observe(Snapshot(10000, 3000, seek: 2, epoch: 3));
session.Observe(Snapshot(11000, 4000, seek: 2, epoch: 3));
Check(session.ListenedMilliseconds == 2000, "Backward seek must rebase and preserve prior listening.");
session.Observe(Snapshot(11000, 5000, playing: false, seek: 2, epoch: 3));
session.Observe(Snapshot(11000, 65000, playing: false, seek: 2, epoch: 3));
session.Observe(Snapshot(11000, 66000, seek: 2, epoch: 3));
session.Observe(Snapshot(12000, 67000, seek: 2, epoch: 3));
Check(session.ListenedMilliseconds == 3000, "Pause or buffering contributed listening time.");
session.Observe(Snapshot(170000, 68000, seek: 2, epoch: 3));
Check(session.ListenedMilliseconds == 3000, "Unmarked position discontinuity was counted.");
session.Observe(Snapshot(170000, 68000, seek: 2, epoch: 3));
Check(session.ListenedMilliseconds == 3000, "Repeated stale snapshot was counted.");

session = new(song with { DurationSeconds = 600 }, Snapshot(0, 0, duration: 600000), 1);
for (int i = 1; i <= 240; i++) session.Observe(Snapshot(i * 1000, i * 1000, duration: 600000));
Check(session.IsQualified, "Long song must qualify at four minutes.");
session = new(song with { DurationSeconds = 30 }, Snapshot(0, 0, duration: 30000), 1);
for (int i = 1; i <= 30; i++) session.Observe(Snapshot(i * 1000, i * 1000, duration: 30000));
Check(!session.IsQualified, "Songs no longer than 30 seconds must not scrobble.");
session = new(song, Snapshot(0, 0), 1);
session.Observe(Snapshot(2000, 1000, rate: 2));
Check(session.ListenedMilliseconds == 1000, "Playback rate multiplied actual listening time.");
session.Observe(Snapshot(0, 2000, epoch: 2) with { GaplessToken = 123 });
Check(session.ListenedMilliseconds == 1000, "Gapless transition counted next song as old song.");
session = new(song with { DurationSeconds = 0 }, Snapshot(0, 0, duration: 0), 1);
session.Observe(Snapshot(1000, 1000, duration: 180000));
Check(session.Track.DurationSeconds == 180, "Late WebDAV duration did not update qualification.");

string folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var credentials = new LastFmCredentials(new string('a', 32), new string('b', 32));
var vault = new TestVault();
var store = new LastFmStore(vault, Path.Combine(folder, "LastFm.json"));
await using var server = new ApiServer();
server.DurableProbe = timestamp => StoreChecks.ReadState(store).Contains(timestamp);
using var client = new LastFmApiClient(new HttpClient(), server.Endpoint);
LastFmService service = new(client, store);
await service.StartAsync();
Check(service.Status.IsReady && !service.Status.Enabled, "New integration must default to disabled.");
Check(vault.Reads == 0, "Unconfigured integration touched Windows credentials during startup.");
await service.ConfigureAsync(credentials.ApiKey, credentials.SharedSecret);
var authorization = await service.BeginAuthorizationAsync();
Check(authorization.Host == "www.last.fm" && authorization.Query.Contains("token=test-token"), "Wrong desktop authorization URL.");
server.AuthorizationGranted = false;
try { await service.CompleteAuthorizationAsync(); throw new Exception("Unapproved token accepted."); }
catch (LastFmApiException ex) { Check(ex.Code == 14 && service.Status.AuthorizationPending, "Pending authorization cannot be retried."); }
server.AuthorizationGranted = true;
await service.CompleteAuthorizationAsync();
Check(service.Status.IsAuthenticated && service.Status.UserName == "alice" && service.Status.Enabled, "Authorized account was not connected.");
Check(!StoreChecks.ReadState(store).Contains(credentials.SharedSecret), "Shared secret leaked into queue file.");
await service.NowPlayingAsync(song, service.Status.Revision, CancellationToken.None);
await WaitFor(() => server.NowPlaying.Count == 1);
Check(server.NowPlaying[0]["artist"] == song.Artist && server.NowPlaying[0]["track"] == song.Title,
    "Form encoding damaged Unicode, ampersands or plus signs.");
Check(!server.SignatureError, "HTTP requests did not contain a valid Last.fm signature.");

using (var blocked = new StoreWriteBlocker(store))
{
    try { await service.EnqueueAsync(song, 1699999999, Guid.NewGuid(), service.Status.Revision); throw new Exception("Failed commit acknowledged."); }
    catch (SQLiteException) { Check(service.Status.PendingCount == 0 && server.ScrobbleRequests == 0, "Uncommitted record was sent or acknowledged."); }
}

server.ScrobbleFailure = 503;
Guid first = Guid.NewGuid();
long revision = service.Status.Revision;
Check(await service.EnqueueAsync(song, 1700000000, first, revision), "Qualified play was not queued.");
await WaitFor(() => server.ScrobbleRequests >= 1 && service.Status.Problem == LastFmProblem.Network);
Check(service.Status.PendingCount == 1 && (await store.LoadAsync(default)).Pending[0].Id == first, "Offline record did not survive on disk.");
Check(server.DurableBeforeSend, "Network submission occurred before durable queue commit.");
await service.EnqueueAsync(song, 1700000000, first, revision);
Check(service.Status.PendingCount == 1, "Duplicate qualification created duplicate queue records.");
await service.SetEnabledAsync(false);
int requestCount = server.ScrobbleRequests;
await service.RetryAsync();
await Task.Delay(120);
Check(server.ScrobbleRequests == requestCount, "Disabled integration still sent queued plays.");
await service.StopAsync();
await service.StopAsync();

server.ScrobbleFailure = 0;
service = new(client, store);
await service.StartAsync();
Check(!service.Status.Enabled && service.Status.PendingCount == 1, "Disabled state or pending queue lost across restart.");
await service.SetEnabledAsync(true);
await WaitFor(() => service.Status.PendingCount == 0 && server.Accepted.Count == 1);
Check(server.Accepted[0]["timestamp"] == "1700000000", "Offline retry replaced original start timestamp.");
Check((await store.LoadAsync(default)).Pending.Count == 0, "Accepted record was not removed durably.");

server.ScrobbleFailure = 503;
await service.EnqueueAsync(song, 1700000001, Guid.NewGuid(), service.Status.Revision);
await WaitFor(() => service.Status.Problem == LastFmProblem.Network);
await service.EnqueueAsync(song with { Title = "second" }, 1700000002, Guid.NewGuid(), service.Status.Revision);
server.ScrobbleFailure = 0;
await service.RetryAsync();
await WaitFor(() => service.Status.PendingCount == 0);
Check(server.Accepted.Count == 3 && server.Accepted[1]["timestamp"] == "1700000001" && server.Accepted[2]["timestamp"] == "1700000002",
    "Offline queue was not submitted oldest first.");

server.ScrobbleFailure = 9;
await service.EnqueueAsync(song, 1700000003, Guid.NewGuid(), service.Status.Revision);
await WaitFor(() => !service.Status.IsAuthenticated);
Check(service.Status.Problem == LastFmProblem.SessionExpired && service.Status.PendingCount == 1,
    "Revoked authorization must stop submissions and preserve pending plays.");
server.ScrobbleFailure = 503;
await service.BeginAuthorizationAsync();
await service.CompleteAuthorizationAsync();
await WaitFor(() => service.Status.Problem == LastFmProblem.Network);
Check(service.Status.PendingCount == 1, "Reauthorizing same account lost offline plays.");
server.UserName = "bob";
await service.BeginAuthorizationAsync();
await service.CompleteAuthorizationAsync();
Check(service.Status.PendingCount == 0 && service.Status.UserName == "bob", "Previous account queue leaked into next login.");

server.IgnoreScrobble = true;
server.ScrobbleFailure = 0;
await service.EnqueueAsync(song, 1700000004, Guid.NewGuid(), service.Status.Revision);
await WaitFor(() => service.Status.PendingCount == 0);
Check(service.Status.Problem == LastFmProblem.Rejected, "HTTP success hid Last.fm metadata rejection.");
server.IgnoreScrobble = false;
StoreWriteBlocker? blockedWrite = null;
server.BeforeScrobbleResponse = () => blockedWrite = new StoreWriteBlocker(store);
try
{
    await service.EnqueueAsync(song, 1700000005, Guid.NewGuid(), service.Status.Revision);
    await WaitFor(() => service.Status.Problem == LastFmProblem.Storage && service.Status.PendingCount == 1);
    int acknowledgedCount = server.Accepted.Count;
    server.BeforeScrobbleResponse = null;
    blockedWrite?.Dispose();
    blockedWrite = null;
    await service.RetryAsync();
    await WaitFor(() => service.Status.PendingCount == 0);
    Check(server.Accepted.Count == acknowledgedCount, "Failed local removal resent an already acknowledged scrobble.");
}
finally
{
    server.BeforeScrobbleResponse = null;
    blockedWrite?.Dispose();
    blockedWrite = null;
}
server.HoldNowPlaying = true;
var held = service.NowPlayingAsync(song, service.Status.Revision, CancellationToken.None);
await server.NowPlayingEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await service.DisconnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
await held.WaitAsync(TimeSpan.FromSeconds(5));
Check(!service.Status.IsAuthenticated && !service.Status.Enabled && service.Status.PendingCount == 0, "Disconnect did not stop requests and clear the queue.");
await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
server.ReleaseNowPlaying.TrySetResult();
Check((await store.ReadIdentityAsync(default))?.Account is null, "Disconnect left a stored session key.");

string corruptPath = Path.Combine(folder, "Corrupt.json");
var corruptStore = new LastFmStore(vault, corruptPath);
await File.WriteAllTextAsync(corruptPath, "{broken");
service = new(client, corruptStore);
try { await service.StartAsync(); throw new Exception("Corrupt queue silently accepted."); }
catch (JsonException) { Check(!service.Status.IsReady, "Corrupt queue became writable."); }
Check(await File.ReadAllTextAsync(corruptPath) == "{broken", "Corrupt queue was overwritten with an empty one.");
await service.StopAsync();
await store.SaveAsync(new LastFmSavedState { HasCredentials = true }, default);
var delayedVault = new DelayedVault();
service = new(client, new LastFmStore(delayedVault, Path.Combine(folder, "LastFm.json")));
var starting = service.StartAsync();
await delayedVault.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
await service.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
try { await starting; throw new Exception("Canceled startup completed."); }
catch (OperationCanceledException) { Check(!service.Status.IsReady, "Shutdown during startup published ready state."); }
await HistoryRegression.RunAsync(Check);
await OldHistoryRegression.RunAsync(Check);
await HistoryFixRegression.RunAsync(Check);
await ApiResponseRegression.RunAsync(Check);
await FavoriteRegression.RunAsync(Check);
Console.WriteLine($"PASS: {checks} checks; listening, seeking, authorization, signed loopback HTTP, real SQLite history import, pagination, deduplication, persistence, cancellation, account isolation and shutdown.");

static async Task WaitFor(Func<bool> condition)
{
    for (int i = 0; i < 500; i++) { if (condition()) return; await Task.Delay(10); }
    throw new TimeoutException("Expected state did not arrive.");
}

sealed class TestVault : ILastFmCredentialVault
{
    private string? _value;
    public int Reads;
    public Task<string?> ReadAsync(CancellationToken token) { Reads++; return Task.FromResult(_value); }
    public Task WriteAsync(string? value, CancellationToken token) { _value = value; return Task.CompletedTask; }
}

sealed class DelayedVault : ILastFmCredentialVault
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<string?> ReadAsync(CancellationToken token) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return null; }
    public Task WriteAsync(string? value, CancellationToken token) => Task.CompletedTask;
}

sealed class ApiServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly Task _loop;
    private readonly List<Task> _requests = [];
    public Uri Endpoint { get; }
    public bool AuthorizationGranted = true, SignatureError, DurableBeforeSend = true, IgnoreScrobble, HoldNowPlaying;
    public Func<string, bool>? DurableProbe;
    public Action? BeforeScrobbleResponse;
    public int ScrobbleFailure, ScrobbleRequests;
    public int RecentFailure, RecentRequests;
    public Action<int>? RecentPageRequested;
    public bool HoldRecent, MalformedRecent, SingleRecent;
    public LastFmRecentPlay? RecentNowPlaying;
    public List<LastFmRecentPlay> RemoteHistory { get; } = [];
    public TaskCompletionSource RecentEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseRecent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string UserName = "alice";
    public List<Dictionary<string, string>> Accepted { get; } = [];
    public List<Dictionary<string, string>> NowPlaying { get; } = [];
    public TaskCompletionSource NowPlayingEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseNowPlaying { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ApiServer()
    {
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Endpoint = new Uri($"http://127.0.0.1:{port}/");
        _loop = LoopAsync();
    }
    private async Task LoopAsync()
    {
        try { while (true) _requests.Add(HandleAsync(await _listener.AcceptTcpClientAsync())); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient connection)
    {
        try
        {
            using var stream = connection.GetStream();
            var header = new StringBuilder();
            byte[] single = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await stream.ReadAsync(single) == 0) return;
                header.Append((char)single[0]);
                if (header.Length > 16384) throw new IOException("Invalid HTTP request.");
            }
            int length = int.Parse(header.ToString().Split("\r\n")
                .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
            byte[] requestBody = new byte[length];
            await stream.ReadExactlyAsync(requestBody);
            string body = Encoding.UTF8.GetString(requestBody);
            int status = 200;
            var data = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string pair in body.Split('&'))
            {
                string[] parts = pair.Split('=', 2);
                data[WebUtility.UrlDecode(parts[0])] = WebUtility.UrlDecode(parts[1]);
            }
            var signed = new SortedDictionary<string, string>(data, StringComparer.Ordinal);
            signed.Remove("api_sig");
            signed.Remove("format");
            string independent = string.Concat(signed.Select(pair => pair.Key + pair.Value)) + new string('b', 32);
            string expected = Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(independent)));
            SignatureError |= data["api_sig"] != expected;
            string response;
            switch (data["method"])
            {
                case "auth.getToken": response = "{\"token\":\"test-token\"}"; break;
                case "auth.getSession": response = AuthorizationGranted
                    ? JsonSerializer.Serialize(new { session = new { name = UserName, key = "test-session" } }) : "{\"error\":14}"; break;
                case "track.updateNowPlaying":
                    lock (NowPlaying) NowPlaying.Add(data);
                    if (HoldNowPlaying) { NowPlayingEntered.TrySetResult(); await ReleaseNowPlaying.Task; }
                    response = "{\"nowplaying\":{}}";
                    break;
                case "user.getRecentTracks":
                    Interlocked.Increment(ref RecentRequests);
                    RecentPageRequested?.Invoke(int.Parse(data["page"]));
                    if (HoldRecent) { RecentEntered.TrySetResult(); await ReleaseRecent.Task; }
                    if (RecentFailure != 0) { status = RecentFailure; response = "{}"; break; }
                    if (MalformedRecent) { response = "{\"recenttracks\":{}}"; break; }
                    var plays = new List<LastFmRecentPlay>(RemoteHistory);
                    lock (Accepted) plays.AddRange(Accepted.Select(play => new LastFmRecentPlay(play["track"], play["artist"], long.Parse(play["timestamp"]))));
                    plays = plays.Where(play => play.StartedAt > long.Parse(data["from"]) && play.StartedAt < long.Parse(data["to"]))
                        .OrderByDescending(play => play.StartedAt).ToList();
                    var items = plays.Skip((int.Parse(data["page"]) - 1) * 200).Take(200).Select(play => new Dictionary<string, object>
                    {
                        ["name"] = play.Title, ["artist"] = new Dictionary<string, string> { ["#text"] = play.Artist },
                        ["date"] = new { uts = play.StartedAt.ToString() }
                    }).ToList();
                    // A Now Playing item is not part of scrobble history.
                    items.Insert(0, new() { ["name"] = "Before Last.fm", ["artist"] = "Artist", ["@attr"] = new { nowplaying = "true" },
                        ["date"] = new { uts = (RecentNowPlaying?.StartedAt ?? 0).ToString() } });
                    response = JsonSerializer.Serialize(new { recenttracks = new Dictionary<string, object>
                    {
                        ["track"] = SingleRecent ? (object)items.Last() : items,
                        ["@attr"] = new { totalPages = Math.Max(1, (plays.Count + 199) / 200).ToString() }
                    } });
                    break;
                default:
                    DurableBeforeSend &= DurableProbe?.Invoke(data["timestamp"]) == true;
                    Interlocked.Increment(ref ScrobbleRequests);
                    if (ScrobbleFailure >= 100) { status = ScrobbleFailure; response = "{}"; }
                    else if (ScrobbleFailure != 0) response = $"{{\"error\":{ScrobbleFailure}}}";
                    else
                    {
                        if (!IgnoreScrobble) lock (Accepted) Accepted.Add(data);
                        response = IgnoreScrobble
                            ? "{\"scrobbles\":{\"@attr\":{\"accepted\":0},\"scrobble\":{\"ignoredMessage\":{\"code\":1}}}}"
                            : "{\"scrobbles\":{\"@attr\":{\"accepted\":1}}}";
                    }
                    BeforeScrobbleResponse?.Invoke();
                    break;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(response);
            byte[] responseHeader = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(responseHeader);
            await stream.WriteAsync(bytes);
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException) { }
        finally { connection.Dispose(); }
    }
    public async ValueTask DisposeAsync()
    {
        ReleaseNowPlaying.TrySetResult();
        ReleaseRecent.TrySetResult();
        _listener.Stop();
        await _loop;
        await Task.WhenAll(_requests);
    }
}
