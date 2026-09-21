using System.Net;
using System.Text;
using Lyricify.Lyrics.Providers.Web;
using Lyricify.Lyrics.Searchers;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.WebService;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;
using AnimatedWin2dControls.Impressionist;

const string qqSong = """
{"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[{"id":"1","mid":"test","title":"Test Song","interval":180,"singer":[{"name":"Test Artist"}],"album":{"title":"Test Album","mid":"album"}}]}}}}}
""";
const string qqEmpty = """{"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[]}}}}} """;
const string neteaseEmpty = """{"code":200,"result":{"songCount":0,"songs":[]}}""";
int failed = 0;
using var service = new LrcService(NullLogger<LrcService>.Instance);
await Run("网易云不可用仍回退 QQ", async () =>
{
    SetHttp(request => request.RequestUri!.Host.Contains("163.com")
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        : Json(request.RequestUri.AbsolutePath.Contains("musicu.fcg") ? qqSong : QqLyric(0, "[00:00.00]hello")));
    var result = await service.GetMixedLyricsAsync(new Music());
    Check(result.Status == LyricsSearchStatus.Found && result.Lyrics.Contains("hello"));
});
await Run("一方失败另一方无结果仍保留重试", async () =>
{
    SetHttp(request => request.RequestUri!.Host.Contains("163.com")
        ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : Json(qqEmpty));
    Check((await service.GetMixedLyricsAsync(new Music())).Status == LyricsSearchStatus.NetworkError);
});
await Run("双方成功且为空才确认无结果", async () =>
{
    SetHttp(request => Json(request.RequestUri!.Host.Contains("163.com") ? neteaseEmpty : qqEmpty));
    Check((await service.GetMixedLyricsAsync(new Music())).Status == LyricsSearchStatus.NoResult);
});
await Run("QQ 搜索业务错误不能记为无结果", async () =>
{
    SetHttp(_ => Json("""{"code":0,"req_1":{"code":1000}}"""));
    Check((await service.GetLyricsAsync(new Music(), Searchers.QQMusic)).Status == LyricsSearchStatus.NetworkError);
    Check((await service.GetKrcLyricsAsync(new Music())).Status == LyricsSearchStatus.NetworkError);
});
await Run("QQ 歌词业务错误不能记为无结果", async () =>
{
    SetHttp(request => Json(request.RequestUri!.AbsolutePath.Contains("musicu.fcg") ? qqSong : QqLyric(1000, null)));
    Check((await service.GetLyricsAsync(new Music(), Searchers.QQMusic)).Status == LyricsSearchStatus.NetworkError);
});
await Run("有效的空歌词确认无结果", async () =>
{
    SetHttp(request => Json(request.RequestUri!.AbsolutePath.Contains("musicu.fcg") ? qqSong : QqLyric(0, "")));
    Check((await service.GetLyricsAsync(new Music(), Searchers.QQMusic)).Status == LyricsSearchStatus.NoResult);
});
await Run("取消不再发起任何请求", async () =>
{
    int requests = 0;
    SetHttp(_ => { requests++; return Json(qqEmpty); });
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try { await service.GetMixedLyricsAsync(new Music(), cts.Token); throw new Exception("未传播取消"); }
    catch (OperationCanceledException) { }
    Check(requests == 0);
});
// 真正挂起 SendAsync，并延迟取消后的清理；排空屏障不能仅取消等待就返回。
await Run("歌词加载入口传递停止令牌且不发布迟到 UI", async () =>
{
    using var handler = new PendingHandler();
    BaseApi.HttpClient.Dispose();
    BaseApi.HttpClient = new HttpClient(handler);
    await Task.Run(() => LoaderCancellationChecks.Run(service, handler));
});
await Run("退出取消在途搜词并等待 HTTP 清理，不再回退且允许重试", async () =>
{
    var lifecycle = new AppLifecycle();
    var tasks = new ApplicationTasks(lifecycle);
    using var handler = new PendingHandler();
    BaseApi.HttpClient.Dispose();
    BaseApi.HttpClient = new HttpClient(handler);
    var operation = tasks.RunAsync(async token =>
        await service.GetMixedLyricsAsync(new Music(), token));
    await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    lifecycle.TryBeginExit(out _);
    var drain = tasks.DrainAsync();
    try
    {
        await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(!drain.IsCompleted);
    }
    finally { handler.Release.TrySetResult(); }
    try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("未传播取消"); }
    catch (OperationCanceledException) { }
    try { await drain.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException) { }
    Check(handler.Finished && handler.Requests == 1);
    SetHttp(request => Json(request.RequestUri!.Host.Contains("163.com") ? neteaseEmpty
        : request.RequestUri.AbsolutePath.Contains("musicu.fcg") ? qqSong : QqLyric(0, "hello")));
    Check((await service.GetMixedLyricsAsync(new Music())).Status == LyricsSearchStatus.Found);
});
foreach (var stage in new[] { "netease-new-search", "netease-lyrics", "qq-search", "qq-lyrics", "qq-verbatim" })
{
    await Run("在途请求取消: " + stage, async () =>
    {
        using var cts = new CancellationTokenSource();
        using var handler = new PendingHandler();
        BaseApi.HttpClient.Dispose();
        BaseApi.HttpClient = new HttpClient(handler);
        Task operation = stage switch
        {
            "netease-new-search" => Lyricify.Lyrics.Helpers.ProviderHelper.NeteaseApi.SearchNew("test", cts.Token),
            "netease-lyrics" => Lyricify.Lyrics.Helpers.ProviderHelper.NeteaseApi.GetLyric("1", cts.Token),
            "qq-search" => service.GetLyricsAsync(new Music(), Searchers.QQMusic, cts.Token),
            "qq-lyrics" => Lyricify.Lyrics.Helpers.ProviderHelper.QQMusicApi.GetLyric("test", cts.Token),
            _ => Lyricify.Lyrics.Helpers.ProviderHelper.QQMusicApi.GetLyricsAsync("1", cts.Token)
        };
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        try { await handler.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { handler.Release.TrySetResult(); }
        try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("未传播取消"); }
        catch (OperationCanceledException) { }
        Check(handler.Finished && handler.Requests == 1);
    });
}
BaseApi.HttpClient.Dispose();
await Run("网易云业务错误与有效空结果分开", async () =>
{
    SetHttp(_ => Json("""{"code":500}"""));
    Check((await service.GetLyricsAsync(new Music(), Searchers.Netease)).Status == LyricsSearchStatus.NetworkError);
    SetHttp(_ => Json("""{"code":200,"result":{"songCount":0}}"""));
    Check((await service.GetLyricsAsync(new Music(), Searchers.Netease)).Status == LyricsSearchStatus.NoResult);
});
await Run("QQ 逐字歌词协议错误保留重试", async () =>
{
    foreach (string response in new[] { "<result><retcode>1</retcode></result>", "<result/>", "<result><content>broken</content></result>" })
    {
        SetHttp(request => Json(request.RequestUri!.AbsolutePath.Contains("musicu.fcg") ? qqSong : response));
        Check((await service.GetKrcLyricsAsync(new Music())).Status == LyricsSearchStatus.NetworkError);
    }
    SetHttp(request => Json(request.RequestUri!.AbsolutePath.Contains("musicu.fcg") ? qqSong : "<result><retcode>0</retcode><content/></result>"));
    Check((await service.GetKrcLyricsAsync(new Music())).Status == LyricsSearchStatus.NoResult);
});
BaseApi.HttpClient.Dispose();
await Run("断路器只允许一个并发探测且取消后可重试", () =>
{
    var breaker = new LyricsSearchCircuitBreaker(3, 100);
    Check(breaker.TryBegin(0, out int stale));
    for (int i = 0; i < 3; i++)
    {
        Check(breaker.TryBegin(0, out int generation));
        breaker.Complete(generation, LyricsSearchStatus.NetworkError, 0);
    }
    Check(!breaker.TryBegin(99, out _));
    breaker.Complete(stale, LyricsSearchStatus.Found, 100);
    int probes = 0, probeGeneration = -1;
    Parallel.For(0, 32, _ =>
    {
        if (breaker.TryBegin(100, out int generation))
        {
            Interlocked.Increment(ref probes);
            probeGeneration = generation;
        }
    });
    Check(probes == 1);
    breaker.Complete(probeGeneration, null, 100);
    Check(breaker.TryBegin(100, out probeGeneration));
    breaker.Complete(probeGeneration, LyricsSearchStatus.NetworkError, 100);
    Check(!breaker.TryBegin(199, out _));
    Check(breaker.TryBegin(200, out probeGeneration));
    breaker.Complete(probeGeneration, LyricsSearchStatus.NoResult, 200);
    Check(breaker.TryBegin(200, out _));
    Check(breaker.TryBegin(200, out _));
    return Task.CompletedTask;
});
await Run("封面缓存缺失回退后仍随主题刷新", () =>
{
    var state = new CoverLoadState();
    Check(state.Begin("missing", true, out int version));
    state.Commit(version, "missing", usesDefault: true, isDark: true);
    Check(!state.Begin("missing", true, out _));
    Check(state.Begin("missing", false, out version));
    state.Commit(version, "missing", usesDefault: true, isDark: false);
    Check(!state.Begin("missing", false, out _));
    return Task.CompletedTask;
});
await Run("A 到 B 再回 A 不接受 B 的迟到结果", () =>
{
    var state = new CoverLoadState();
    Check(state.Begin("A", false, out int version));
    state.Commit(version, "A", usesDefault: false, isDark: false);
    Check(state.Begin("B", false, out int stale));
    Check(!state.Begin("A", true, out _));
    state.Commit(stale, "B", usesDefault: false, isDark: false);
    Check(!state.IsCurrent(stale));
    Check(!state.Begin("A", true, out _));
    state.Reset();
    Check(state.Begin("A", true, out _));
    return Task.CompletedTask;
});
await Run("默认封面初次加载、失败重试及空哈希复用", () =>
{
    var state = new CoverLoadState();
    Check(state.Begin(null, false, out _));
    Check(state.Begin(null, false, out int version)); // 未提交即失败，可重试
    state.Commit(version, null, usesDefault: true, isDark: false);
    Check(!state.Begin("", false, out _));
    Check(state.Begin(null, true, out _));
    return Task.CompletedTask;
});
await Run("默认调色板并发只计算一次，主题和算法独立缓存", async () =>
{
    int loads = 0;
    var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cache = new DefaultCoverPaletteCache(async (_, _, token) =>
    {
        Interlocked.Increment(ref loads);
        await ready.Task.WaitAsync(token);
        return new PaletteResult([], false, new ThemeColorResult(default, false));
    });
    var first = cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default).AsTask();
    var second = cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default).AsTask();
    ready.SetResult();
    Check(ReferenceEquals(await first, await second));
    Check(loads == 1);
    await cache.GetAsync(true, PaletteAlgorithm.KMeansPP, default);
    await cache.GetAsync(false, PaletteAlgorithm.OctTree, default);
    await cache.GetAsync(true, PaletteAlgorithm.OctTree, default);
    Check(loads == 4);
    for (int i = 0; i < 100; i++) await cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default);
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++)
    {
        var hit = cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default);
        Check(hit.IsCompletedSuccessfully && ReferenceEquals(hit.Result, first.Result));
    }
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(loads == 4 && allocated == 0);
    Console.WriteLine($"  缓存命中 1000 次，托管分配 {allocated} 字节");
});
await Run("缓存失败和取消不会污染后续加载", async () =>
{
    int loads = 0;
    var cache = new DefaultCoverPaletteCache((_, _, token) =>
    {
        loads++;
        if (loads == 1) throw new IOException("fixture");
        return Task.FromResult<PaletteResult?>(new PaletteResult([], false, new ThemeColorResult(default, false)));
    });
    try { await cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default); throw new Exception("未抛出异常"); }
    catch (IOException) { }
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    try { await cache.GetAsync(false, PaletteAlgorithm.KMeansPP, cts.Token); throw new Exception("未传播取消"); }
    catch (OperationCanceledException) { }
    Check(await cache.GetAsync(false, PaletteAlgorithm.KMeansPP, default) is not null && loads == 2);
});
return failed == 0 ? 0 : 1;

async Task Run(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failed++; Console.WriteLine($"FAIL {name}: {ex.Message}"); }
}
static void Check(bool condition) { if (!condition) throw new Exception("断言失败"); }
static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
static string QqLyric(int code, string? lyrics) => "MusicJsonCallback_lrc(" + System.Text.Json.JsonSerializer.Serialize(new
{
    code, lyric = lyrics is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(lyrics))
}) + ")";
static void SetHttp(Func<HttpRequestMessage, HttpResponseMessage> respond)
{
    BaseApi.HttpClient.Dispose();
    BaseApi.HttpClient = new HttpClient(new StubHandler(respond));
}
sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(respond(request));
}

sealed class PendingHandler : HttpMessageHandler
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Requests;
    public bool Finished;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Interlocked.Increment(ref Requests);
        Started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, token);
            throw new Exception("请求未被取消");
        }
        catch (OperationCanceledException)
        {
            Cancelled.TrySetResult();
            await Release.Task;
            throw;
        }
        finally { Finished = true; }
    }
}
