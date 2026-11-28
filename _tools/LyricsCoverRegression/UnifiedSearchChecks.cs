using System.Net;
using Lyricify.Lyrics.Providers.Web;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.WebService;

internal static class UnifiedSearchChecks
{
    public static async Task RunAsync(LrcService provider)
    {
        var db = new SQLiteAsyncConnection(Path.Combine(Path.GetTempPath(), "lyrics-search-" + Guid.NewGuid().ToString("N") + ".db"));
        await db.ExecuteAsync("CREATE TABLE Music(Id INTEGER PRIMARY KEY)");
        await db.ExecuteAsync("INSERT INTO Music(Id) VALUES(1),(2)");
        await db.CreateTableAsync<MusicLyrics>();
        var database = new MusicDatabaseService(db, new());
        await database.Lyrics.InitializeAsync();
        using var search = new LyricsOnlineSearch(database, provider, new(), NullLogger<LyricsOnlineSearch>.Instance);
        var empty = new EmptyHandler();
        BaseApi.HttpClient.Dispose(); BaseApi.HttpClient = new(empty);
        var music = new Music { Id = 1, Path = "library.flac", IsKrcSearched = true, IsLrcSearched = true };
        await database.Lyrics.SetSearchAsync(music.Id, "qq-word", "old-policy", "NoResult");
        await search.SearchAsync(music, false, default);
        int initialRequests = empty.Calls;
        int states = await db.Table<LyricsSearchStateRecord>().CountAsync();
        Check(initialRequests > 0 && states == 4, $"provider states are distinct: requests={initialRequests}, states={states}");
        await search.SearchAsync(music, false, default);
        Check(empty.Calls == initialRequests, "unchanged NoResult skips network");
        music.Title = "Metadata changed";
        await search.SearchAsync(music, false, default);
        Check(empty.Calls == initialRequests * 2, "metadata changes invalidate old NoResult");
        await Task.WhenAll(search.SearchAsync(music, true, default), search.SearchAsync(music, true, default));
        Check(empty.Calls == initialRequests * 4, "both explicit requests execute despite cached NoResult");
        using var pending = new PendingHandler();
        BaseApi.HttpClient.Dispose(); BaseApi.HttpClient = new(pending);
        var operation = search.SearchAsync(new Music { Id = 2 }, false, default);
        await pending.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AppSettings.IsAutoLyricsEnabled = false;
        search.CancelAutomaticSearch();
        await pending.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!operation.IsCompleted, "automatic cancellation waits for real HTTP cleanup");
        pending.Release.TrySetResult();
        Check(await operation is null && await db.Table<LyricsSearchStateRecord>().Where(s => s.MusicId == 2).CountAsync() == 0, "cancel does not become NoResult");
        var later = new EmptyHandler();
        BaseApi.HttpClient.Dispose(); BaseApi.HttpClient = new(later);
        await search.SearchAsync(new Music { Id = 2 }, false, default);
        Check(later.Calls == 0, "disabled automatic search rejects new requests");
        await search.SearchAsync(new Music { Id = 2 }, true, default);
        Check(later.Calls == initialRequests, "manual request still works while automatic search disabled");
        AppSettings.IsAutoLyricsEnabled = true;
        await db.CloseAsync();
    }
    private static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
    private sealed class EmptyHandler : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Calls++;
            string host = request.RequestUri!.Host;
            string body = host switch
            {
                "mobilecdn.kugou.com" => """{"status":1,"errcode":0,"data":{"total":0,"info":[]}}""",
                "lrclib.net" => "[]",
                var h when h.Contains("163.com") => """{"code":200,"result":{"songCount":0,"songs":[]}}""",
                _ => """{"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[]}}}}}"""
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
