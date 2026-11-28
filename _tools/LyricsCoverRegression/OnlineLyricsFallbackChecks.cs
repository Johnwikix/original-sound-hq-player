using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Lyricify.Lyrics.Providers.Web;
using Lyricify.Lyrics.Decrypter.Qrc;
using Microsoft.Extensions.Logging.Abstractions;
using SQLite;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.Lyrics;
using WinUIMusicPlayer.WebService;

internal static class OnlineLyricsFallbackChecks
{
    private const string KugouEmpty = """{"status":1,"errcode":0,"data":{"total":0,"info":[]}}""";
    private const string KugouTrack = """
        {"status":1,"errcode":0,"data":{"total":1,"info":[{"hash":"test","songname":"Test Song","singername":"Test Artist","album_name":"Test Album","duration":180}]}}
        """;
    private const string KugouLyrics = """{"status":200,"errcode":200,"candidates":[{"id":"1","accesskey":"sample","duration":180000}]}""";
    private const string QqEmpty = """{"code":0,"req_1":{"code":0,"data":{"code":0,"body":{"song":{"list":[]}}}}}""";
    private const string QqNoLyrics = "<result><retcode>0</retcode><content/></result>";
    private const string NeteaseEmpty = """{"code":200,"result":{"songCount":0,"songs":[]}}""";
    private const string NeteaseTrack = """
        {"code":200,"result":{"songCount":1,"songs":[{"id":1,"name":"Test Song","dt":180000,"ar":[{"name":"Test Artist"}],"al":{"name":"Test Album","id":1}}]}}
        """;
    private const string NeteaseLyrics = """{"code":200,"lrc":{"lyric":"[00:01.00]Netease line"}}""";
    private const string QqTrack = """
        {"id":1,"mid":"test","title":"<em>Test Song</em>","interval":180,"singer":[{"name":"<em>Test Artist</em>"}],"album":{"title":"Test Album","mid":"album"}}
        """;

    public static async Task KugouFallbackAsync()
    {
        const string krc = "[1000,600]<0,300,0>Ku<300,300,0>gou\n[2000,500]<0,500,0>word";
        string payload = JsonSerializer.Serialize(new { status = 200, error_code = 0, content = EncryptKrc(krc) });
        using var handler = SetHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
        {
            "/cgi-bin/musicu.fcg" => QqMobile($"[{QqTrack}]"),
            "/qqmusic/fcgi-bin/lyric_download.fcg" => QqNoLyrics,
            "/api/v3/search/song" => KugouTrack,
            "/search" => KugouLyrics,
            "/download" => payload,
            _ => throw new InvalidOperationException("No provider should be queried after Kugou supplies the fallback"),
        }));
        var doc = await SearchAsync(new Music(), language: "en");
        Check(doc?.Original.Format == LyricsFormat.Krc && doc.Original.Content.Contains("gou"), "Decode Kugou KRC after QQ confirms no results");
        Check(handler.Requests.SequenceEqual(new[] { "/cgi-bin/musicu.fcg", "/qqmusic/fcgi-bin/lyric_download.fcg", "/api/v3/search/song", "/search", "/download" }), "QQ precedes KRC search and download");
    }

    public static async Task ChineseTranslationPriorityAsync()
    {
        const string krc = "[1000,600]<0,300,0>Ku<300,300,0>gou";
        const string qrc = "[1000,400]QQ(1000,400)\n[1500,400]word(1500,400)";
        const string krcTranslation = """{"content":[{"type":1,"lyricContent":[["酷狗译文"]]}]}""";
        string krcWithTranslation = $"[language:{Convert.ToBase64String(Encoding.UTF8.GetBytes(krcTranslation))}]\n{krc}";
        var cases = new[]
        {
            (Name: "有翻译的逐行优先于无翻译逐字", Language: "zh", Kugou: krc,
                QqTranslation: "", NeteaseTranslation: "[00:01.00]网易云译文", Format: LyricsFormat.Lrc, Translation: "网易云译文", Requests: 7),
            (Name: "同有中文译文时 QQ 优先", Language: "zh-Hant", Kugou: krcWithTranslation,
                QqTranslation: "[00:01.00]逐字譯文", NeteaseTranslation: "[00:01.00]网易云译文", Format: LyricsFormat.Qrc, Translation: "逐字譯文", Requests: 2),
            (Name: "错位译文回退首个逐字结果", Language: "zh-CN", Kugou: krc,
                QqTranslation: "[00:15.00]错位译文", NeteaseTranslation: "[00:16.00]错位译文", Format: LyricsFormat.Qrc, Translation: "", Requests: 7),
            (Name: "日文译文不盖过中文译文", Language: "zh", Kugou: krc,
                QqTranslation: "[00:01.00]漢字\n[00:01.50]かな", NeteaseTranslation: "[00:01.00]网易云译文", Format: LyricsFormat.Lrc, Translation: "网易云译文", Requests: 7),
            (Name: "酷狗内嵌中文译文命中即返回", Language: "zh", Kugou: krcWithTranslation,
                QqTranslation: "", NeteaseTranslation: "[00:01.00]网易云译文", Format: LyricsFormat.Krc, Translation: "酷狗译文", Requests: 5),
            (Name: "非中文界面首个有效逐字命中即返回", Language: "ja", Kugou: krc,
                QqTranslation: "", NeteaseTranslation: "[00:01.00]网易云译文", Format: LyricsFormat.Qrc, Translation: "", Requests: 2),
        };
        foreach (var test in cases)
        {
            string kugou = JsonSerializer.Serialize(new { status = 200, error_code = 0, content = EncryptKrc(test.Kugou) });
            string qqTranslation = test.QqTranslation.Length == 0 ? "" : EncryptQrc(test.QqTranslation);
            string qq = $"<result><retcode>0</retcode><content>{EncryptQrc(qrc)}</content><contentts>{qqTranslation}</contentts></result>";
            string netease = JsonSerializer.Serialize(new
            {
                code = 200, lrc = new { lyric = "[00:01.00]Netease line" }, tlyric = new { lyric = test.NeteaseTranslation },
            });
            using var handler = SetHandler(request => JsonResponse(request.RequestUri!.AbsolutePath switch
            {
                "/api/v3/search/song" => KugouTrack,
                "/search" => KugouLyrics,
                "/download" => kugou,
                "/cgi-bin/musicu.fcg" => QqMobile($"[{QqTrack}]"),
                "/qqmusic/fcgi-bin/lyric_download.fcg" => qq,
                "/eapi/cloudsearch/pc" => NeteaseTrack,
                "/weapi/song/lyric" => netease,
                _ => throw new InvalidOperationException("Translation ranking should not reach LRCLIB when lyrics already exist"),
            }));
            var doc = await SearchAsync(new Music(), language: test.Language) ?? throw new InvalidOperationException(test.Name);
            Check(doc.Original.Format == test.Format, test.Name);
            var visibleTranslation = new LyricsParser().GetAlignedTranslationLines(doc, default);
            Check(test.Translation.Length == 0 ? !visibleTranslation.Any() :
                visibleTranslation.Contains(test.Translation), test.Name);
            Check(handler.Requests.Count == test.Requests, $"{test.Name}: {handler.Requests.Count} requests, expected {test.Requests}");
        }
    }

    public static async Task QqMobileAsync()
    {
        const string wrongVersion = """
            {"id":2,"mid":"wrong","title":"Test Song","interval":180,"singer":[{"name":"Test Artist"}],"album":{"title":"Test Album","mid":"album"}}
            """;
        string correctVersion = QqTrack.Replace("</em>", "</em> (Taylor's Version)", StringComparison.Ordinal)
            .Replace("<em>Test Artist</em> (Taylor's Version)", "<em>Test Artist</em>", StringComparison.Ordinal);
        const string qrc = "[1000,600]Cor(1000,300)rect(1300,300)";
        using var handler = SetHandler(async (request, token) =>
        {
            return request.RequestUri!.AbsolutePath switch
            {
                "/api/v3/search/song" => JsonResponse(KugouEmpty),
                "/cgi-bin/musicu.fcg" => JsonResponse(await VerifyMobileSearchAsync(
                    request, token, QqMobile($"[{wrongVersion},{correctVersion}]"))),
                "/qqmusic/fcgi-bin/lyric_download.fcg" => JsonResponse(await VerifyQqSongAsync(request, token, qrc)),
                _ => throw new InvalidOperationException("Expected matched QQ QRC to stop fallback"),
            };
        });
        var doc = await SearchAsync(new Music { Title = "Test Song (Taylor's Version)" }, language: "en");
        Check(doc?.Original.Format == LyricsFormat.Qrc && doc.Original.Content.Contains("Cor("), "Rank eligible versions before downloading");
        Check(handler.Requests.SequenceEqual(new[] { "/cgi-bin/musicu.fcg", "/qqmusic/fcgi-bin/lyric_download.fcg" }),
            "First-priority QQ hit requires only one mobile search and one download");
    }

    public static async Task NeteaseAsync()
    {
        using var handler = SetHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path == "/eapi/cloudsearch/pc")
                Check(request.Headers.Contains("Cookie"), "Netease eAPI keeps its own cookie");
            if (path.Contains("musicu.fcg"))
                Check(!request.Headers.Contains("Cookie"), "Mobile QQ request has no unrelated cookie");
            return JsonResponse(path switch
            {
                "/api/v3/search/song" => KugouEmpty,
                "/cgi-bin/musicu.fcg" => QqMobile($"[{QqTrack}]"),
                "/qqmusic/fcgi-bin/lyric_download.fcg" => "<result><retcode>0</retcode><content>[00:01.00]QQ line</content></result>",
                "/eapi/cloudsearch/pc" => NeteaseTrack,
                "/weapi/song/lyric" => NeteaseLyrics,
                _ => throw new InvalidOperationException("Expected new Netease search to satisfy the request"),
            });
        });
        var doc = await SearchAsync(new Music());
        Check(doc?.Original.Content.Contains("Netease line") == true, "QQ line lyrics do not consume the QRC slot");
        Check(handler.Requests.Contains("/eapi/cloudsearch/pc") && !handler.Requests.Contains("/api/search/get/web"),
            "New Netease search is preferred");
    }

    public static async Task LrcLibAsync()
    {
        const string lrcLibResult = """
            [{"trackName":"Test Song","artistName":"Test Artist","albumName":"Test Album","duration":180,"syncedLyrics":"[00:01.00]wrong version"},
             {"trackName":"Test Song (Acoustic)","artistName":"Test Artist","albumName":"Test Album","duration":185,"syncedLyrics":"[00:01.00]wrong duration"},
             {"trackName":"Test Song (Acoustic)","artistName":"Someone Else","albumName":"Test Album","duration":180,"syncedLyrics":"[00:01.00]wrong artist"},
             {"trackName":"Test Song (Acoustic)","artistName":"Test Artist","albumName":"Test Album","duration":180,"plainLyrics":"plain","syncedLyrics":null},
             {"trackName":"Test Song (Acoustic)","artistName":"Test Artist","albumName":"Test Album","duration":180,"syncedLyrics":"[00:01.00]LRCLIB match"}]
            """;
        using var handler = SetHandler(request =>
        {
            if (request.RequestUri!.Host == "mobilecdn.kugou.com")
                return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            return JsonResponse(request.RequestUri.AbsolutePath switch
            {
                "/cgi-bin/musicu.fcg" => QqEmpty,
                "/eapi/cloudsearch/pc" => NeteaseEmpty,
                "/api/search" => lrcLibResult,
                _ => throw new InvalidOperationException("Unexpected LRCLIB fallback request"),
            });
        });
        var doc = await SearchAsync(new Music { Title = "Test Song (Acoustic)" });
        Check(doc?.Original.Content.Contains("LRCLIB match") == true, "Select version-compatible synced LRC through a provider outage");
        Check(handler.Requests[0] == "/cgi-bin/musicu.fcg" && handler.Requests.Contains("/api/v3/search/song") &&
            handler.Requests[^1] == "/api/search", "LRCLIB runs after QQ and Kugou");
    }

    public static async Task CancelDownloadAsync()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = SetHandler(async (request, token) =>
        {
            switch (request.RequestUri!.AbsolutePath)
            {
                case "/cgi-bin/musicu.fcg": return JsonResponse(QqMobile($"[{QqTrack}]"));
                case "/qqmusic/fcgi-bin/lyric_download.fcg": return JsonResponse(QqNoLyrics);
                case "/api/v3/search/song": return JsonResponse(KugouTrack);
                case "/search": return JsonResponse(KugouLyrics);
                case "/download":
                    started.SetResult();
                    try { await Task.Delay(Timeout.Infinite, token); }
                    catch (OperationCanceledException)
                    {
                        cancelled.SetResult();
                        await release.Task;
                        throw;
                    }
                    break;
            }
            throw new InvalidOperationException("Search must not fall back after cancellation");
        });
        using var caller = new CancellationTokenSource();
        var operation = SearchAsync(new Music(), caller.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            caller.Cancel();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!operation.IsCompleted, "Await the real download cleanup");
        }
        finally { release.TrySetResult(); }
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
            throw new InvalidOperationException("The caller cancellation should propagate");
        }
        catch (OperationCanceledException) { }
        Check(handler.Requests.Count == 5, "No extra provider request after cancellation");
    }

    private static async Task<LyricsDocument?> SearchAsync(Music music, CancellationToken token = default, string? language = null)
    {
        using var provider = new LrcService(NullLogger<LrcService>.Instance);
        var parser = new LyricsParser();
        var database = new MusicDatabaseService(new SQLiteAsyncConnection(":memory:"), parser);
        using var search = new LyricsOnlineSearch(database, provider, parser, NullLogger<LyricsOnlineSearch>.Instance);
        string previous = AppData.SystemLanguage;
        if (language is not null) AppData.SystemLanguage = language;
        try { return await search.SearchAsync(music, true, token); }
        finally { AppData.SystemLanguage = previous; }
    }

    private static string QqMobile(string songs) =>
        "{\"code\":0,\"music.search.SearchCgiService.DoSearchForQQMusicMobile\":{\"code\":0,\"data\":{\"code\":0,\"body\":{\"item_song\":" +
        songs + "}}}}";

    private static async Task<string> VerifyMobileSearchAsync(HttpRequestMessage request, CancellationToken token, string result)
    {
        Check(request.RequestUri!.Host == "u6.y.qq.com" &&
            request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded" &&
            request.Headers.UserAgent.ToString().Contains("android"), "Mobile QQ search contract");
        string payload = await request.Content!.ReadAsStringAsync(token);
        Check(payload.Contains("DoSearchForQQMusicMobile", StringComparison.Ordinal), "Mobile method selected");
        return result;
    }

    private static async Task<string> VerifyQqSongAsync(HttpRequestMessage request, CancellationToken token, string qrc)
    {
        string form = await request.Content!.ReadAsStringAsync(token);
        Check(form.Contains("musicid=1", StringComparison.Ordinal), "Never download the original in place of the re-recording");
        return $"<result><retcode>0</retcode><content>{EncryptQrc(qrc)}</content></result>";
    }

    private static string EncryptQrc(string text)
    {
        using var stream = new MemoryStream();
        using (var compressor = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes(text));
        var padded = new byte[((int)stream.Length + 7) / 8 * 8];
        stream.ToArray().CopyTo(padded, 0);

        byte[][][] schedule = [];
        schedule = Enumerable.Range(0, 3)
            .Select(_ => Enumerable.Range(0, 16).Select(_ => new byte[6]).ToArray()).ToArray();
        DESHelper.TripleDESKeySetup(Encoding.ASCII.GetBytes("!@#)(*$%123ZXC!@!@#)(NHL"), schedule, DESHelper.ENCRYPT);
        byte[] encrypted = new byte[padded.Length];
        var block = new byte[8];
        for (int i = 0; i < padded.Length; i += 8)
        {
            DESHelper.TripleDESCrypt(padded[i..], block, schedule);
            block.CopyTo(encrypted, i);
        }
        return Convert.ToHexString(encrypted);
    }

    private static string EncryptKrc(string text)
    {
        byte[] key = [0x40, 0x47, 0x61, 0x77, 0x5e, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2d, 0xce, 0xd2, 0x6e, 0x69];
        using var stream = new MemoryStream();
        stream.Write("krc1"u8);
        using (var compressor = new ZLibStream(stream, CompressionLevel.Fastest, leaveOpen: true))
            compressor.Write(Encoding.UTF8.GetBytes("\uFEFF" + text));
        byte[] data = stream.ToArray();
        for (int i = 4; i < data.Length; i++)
            data[i] ^= key[(i - 4) % key.Length];
        return Convert.ToBase64String(data);
    }

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    private static void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); }

    private static RecordingHandler SetHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => SetHandler((request, _) => Task.FromResult(respond(request)));

    private static RecordingHandler SetHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new RecordingHandler(respond);
        BaseApi.HttpClient.Dispose();
        BaseApi.HttpClient = new HttpClient(handler);
        return handler;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.AbsolutePath);
            return respond(request, cancellationToken);
        }
    }
}
