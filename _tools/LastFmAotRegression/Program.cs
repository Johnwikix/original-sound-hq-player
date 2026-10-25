using System.Net;
using System.Text.Json;
using SQLite;
using WinUIMusicPlayer.Services.LastFm;

int checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
Check(!JsonSerializer.IsReflectionEnabledByDefault, "JSON reflection fallback unexpectedly enabled");
string folder = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var vault = new TestVault();
var store = new LastFmStore(vault, Path.Combine(folder, "LastFm.json"));
var track = new LastFmTrack("Title 中文", "Artist", "Album", 180);
long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
var saved = new LastFmSavedState
{
    HasCredentials = true, Enabled = true,
    Pending = [new(Guid.NewGuid(), new string('a', 32), "alice", track, now - 100, new string('c',64), now - 1000)],
    Known = [new(new string('a',32), "alice", new string('d',64), now - 99)],
    HistoryReceipts = [new("alice", new string('e',64))]
};
await store.SaveAsync(saved, default);
var loaded = await store.LoadAsync(default);
Check(loaded.Pending.SequenceEqual(saved.Pending) && loaded.Known.Count == 0 && loaded.HistoryReceipts.Count == 0, "Small queue graph roundtrip failed");
Check((await store.ReadMappingsAsync("alice", [new string('c',64),new string('e',64)],default)).Count == 2, "SQLite reservation/receipt mappings were trimmed");
Check((await store.ReadKnownAsync("alice",new string('a',32),default)).Single().TrackKey == new string('d',64), "SQLite Known setters were trimmed");
await store.CommitAsync(loaded with { Pending = [] },new(loaded.Pending[0],true),[],default);
var mapping = (await store.ReadMappingsAsync("ALICE",[new string('c',64)],default)).Single().Value;
Check(mapping.Accepted && mapping.Retimed && mapping.SubmittedAt == now-100 && mapping.OriginalStartedAt == now-1000, "SQLite acknowledgement mapping failed");
var aliases = await store.ReadAliasesAsync("alice",[new(LastFmHistory.TrackKey(track.Title,track.Artist),now-100)],default);
Check(aliases.Retimed.Count == 1, "SQLite alias setters were trimmed");
var identity = new LastFmIdentity(new(new string('a',32),new string('b',32)),new("alice","test-session"));
await store.SaveIdentityAsync(identity, default);
Check(await store.ReadIdentityAsync(default) == identity, "Identity roundtrip failed");
string legacy = "{\"Version\":1,\"Enabled\":true,\"HasCredentials\":true,\"Pending\":[{\"Id\":\"110d7383-7c50-4bbc-a161-164875cbb158\",\"ApiKey\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"Account\":\"alice\",\"Track\":{\"Title\":\"Old title\",\"Artist\":\"Artist\",\"Album\":\"\",\"DurationSeconds\":180},\"StartedAt\":1700000000}]}";
await File.WriteAllTextAsync(Path.Combine(folder,"Legacy.json"), legacy);
await using var legacyStore = new LastFmStore(new TestVault(),Path.Combine(folder,"Legacy.json"));
loaded = await legacyStore.LoadAsync(default);
Check(loaded.Pending.Count == 1 && loaded.Pending[0].HistoryKey is null && loaded.Pending[0].OriginalStartedAt == 0 && loaded.Known.Count == 0 && loaded.HistoryReceipts.Count == 0, "Legacy JSON/default ctor parameters failed");

// Intentionally create/insert through SQL: QueryAsync<T> alone must retain mapping and setters.
var database = new SQLiteAsyncConnection(Path.Combine(folder,"history.db"));
await database.ExecuteAsync("CREATE TABLE PlaybackHistory (Id INTEGER PRIMARY KEY AUTOINCREMENT, MusicId INTEGER, Title TEXT, Author TEXT, Album TEXT, TotalDurationMs REAL, DurationPlayedMs REAL, StartedAt BIGINT, EndedAt BIGINT)");
DateTime start = DateTime.UtcNow.AddDays(-1);
for(int i=0;i<501;i++)
    await database.ExecuteAsync("INSERT INTO PlaybackHistory (MusicId,Title,Author,Album,TotalDurationMs,DurationPlayedMs,StartedAt,EndedAt) VALUES(1,?,?,?,?,?,?,?)", "Title 中文 " + i, "Artist", "Album", 180000d, 120000d, start.AddMinutes(i).Ticks, start.AddMinutes(i+2).Ticks);
DateTime old = start.AddDays(-30);
await database.ExecuteAsync("INSERT INTO PlaybackHistory (MusicId,Title,Author,Album,TotalDurationMs,DurationPlayedMs,StartedAt,EndedAt) VALUES(1,?,?,?,?,?,?,?)", "Older", "Artist", "Album", 180000d, 120000d, old.Ticks, old.AddMinutes(2).Ticks);
ILastFmHistorySource source = new SqliteLastFmHistorySource(() => database);
Check(await source.CountOlderAsync(start.AddMinutes(-1),default) == 1, "SQLite scalar count failed");
int rows = 0;
await foreach(var row in source.ReadAsync(start.AddMinutes(-1),DateTime.UtcNow,default))
{
    Check(row.Id == rows+1 && row.Title == "Title 中文 " + rows && row.Artist == "Artist" && row.Album == "Album" && row.TotalDurationMs == 180000 && row.DurationPlayedMs == 120000 && row.StartedAt == start.AddMinutes(rows) && row.EndedAt == start.AddMinutes(rows+2), "SQLite mapped values were trimmed or incorrect");
    rows++;
}
Check(rows == 501,"SQLite interface async pagination failed");
using var canceled = new CancellationTokenSource(); canceled.Cancel();
try { await foreach(var row in source.ReadAsync(start, DateTime.UtcNow, canceled.Token)) {} throw new Exception("Cancellation ignored"); }
catch(OperationCanceledException) { checks++; }
File.Delete(Path.Combine(folder,"LastFm.json"));
await store.SaveAsync(new LastFmSavedState { HasCredentials = true, Enabled = true },default);
using var client = new LastFmApiClient(new HttpClient(new TestHttp()),new Uri("https://lastfm.invalid/"));
var service = new LastFmService(client, store);
await service.StartAsync();
await service.ImportHistoryAsync(source);
Check(service.Status.HistoryImport is { Queued: 501, TooOld: 1 },"Service import/interface async failed");
loaded = await store.LoadAsync(default);
Check(loaded.Pending.Count == 501 && loaded.Pending.All(play => play.HistoryKey is {Length:64} && play.OriginalStartedAt > 0),"Imported durable queue metadata failed");
await service.StopAsync();
await database.CloseAsync();
await ApiResponseRegression.RunAsync(Check);
await FavoriteRegression.RunAsync(Check);
await FavoriteFixRegression.RunAsync(Check);
Console.WriteLine($"PASS {checks} checks. JSON reflection disabled; source-generated legacy/new JSON; SQLite 501-row mapping/paging; interface async cancellation and service import.");

sealed class TestVault : ILastFmCredentialVault
{
    private string? _value;
    public Task<string?> ReadAsync(CancellationToken token) => Task.FromResult(_value);
    public Task WriteAsync(string? value,CancellationToken token) { _value = value; return Task.CompletedTask; }
}
sealed class TestHttp : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        string body = await request.Content!.ReadAsStringAsync(token);
        return body.Contains("user.getRecentTracks",StringComparison.Ordinal)
            ? new(HttpStatusCode.OK) {Content = new StringContent("{\"recenttracks\":{\"track\":[],\"@attr\":{\"totalPages\":\"1\"}}}")}
            : new(HttpStatusCode.ServiceUnavailable) {Content = new StringContent("{}")};
    }
}
