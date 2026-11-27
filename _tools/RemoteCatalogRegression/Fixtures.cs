using System.Net;
using System.Security.Cryptography;
using System.Text;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

internal static class Fixtures
{
    private static readonly Uri Root = new("https://unit.test/reverse-proxy/");
    private const string JellyfinUser = "7ccd2c55aad1492aac251be1379a1d13";

    internal static async Task RunAsync(CancellationToken token)
    {
        await VerifyJellyfinAsync(token);
        await VerifyNavidromeAsync(token);
        await CatalogDatabaseChecks.RunAsync(token);
        Console.WriteLine("PASS authenticated paged catalog, raw media Range, redirect rejection, SQLite migration, stable identity, and interrupted sync");
    }

    private static async Task VerifyJellyfinAsync(CancellationToken token)
    {
        int pages = 0;
        bool rejectLogin = false;
        using var http = new RemoteMediaHttp(new FixtureHandler(async (request, ct) =>
        {
            var uri = request.RequestUri!;
            Program.Check(uri.AbsolutePath.StartsWith("/reverse-proxy/", StringComparison.Ordinal), "lost Jellyfin route prefix");
            string auth = request.Headers.GetValues("Authorization").Single();
            if (uri.AbsolutePath.EndsWith("/Users/AuthenticateByName", StringComparison.Ordinal))
            {
                string body = await request.Content!.ReadAsStringAsync(ct);
                Program.Check(request.Method == HttpMethod.Post && auth.StartsWith("MediaBrowser", StringComparison.Ordinal) &&
                    request.Content.Headers.ContentType is { MediaType: "application/json", CharSet: "utf-8" } &&
                    body.Contains("\"Username\":\"test\"", StringComparison.Ordinal) && body.Contains("\"Pw\":\"secret\"", StringComparison.Ordinal),
                    "Jellyfin authentication header or body");
                if (rejectLogin) return new(HttpStatusCode.Unauthorized);
                return Json($$$"""{"AccessToken":"fixture-auth","User":{"Id":"{{{JellyfinUser}}}"}}""");
            }
            Program.Check(auth.Contains("Token=\"fixture-auth\"", StringComparison.Ordinal)
                && !uri.Query.Contains("fixture-auth", StringComparison.Ordinal), "Jellyfin token not restricted to header");
            if (uri.AbsolutePath.EndsWith("/Items", StringComparison.Ordinal))
            {
                Program.Check(uri.Query.Contains($"userId={JellyfinUser}", StringComparison.Ordinal) &&
                    uri.Query.Contains("includeItemTypes=Audio", StringComparison.Ordinal), "Jellyfin user and audio filter missing");
                pages++;
                int offset = int.Parse(ParseQuery(uri, "startIndex"));
                int limit = int.Parse(ParseQuery(uri, "limit"));
                var items = string.Join(',', Enumerable.Range(offset, Math.Min(limit, 129 - offset)).Select(index =>
                    $$"""{"Id":"{{index:x32}}","AlbumId":"{{JellyfinUser}}","Name":"Track {{index}}","Artists":["Artist"],"RunTimeTicks":10000000,"Container":"flac","MediaSources":[{"Size":4096}]}"""));
                return Json($$"""{"Items":[{{items}}],"TotalRecordCount":129}""");
            }
            Program.Check(uri.AbsolutePath.Contains("/Audio/", StringComparison.Ordinal) && ParseQuery(uri, "static") == "true" &&
                request.Headers.Range?.Ranges.Single().From == 1024, "Jellyfin original media request");
            return new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([42]) };
        }));
        var provider = new JellyfinProvider(http);
        var session = await provider.AuthenticateAsync(Root, "test", "secret", token);
        await provider.ProbeAsync(session, token);
        int count = 0;
        CatalogTrack? last = null;
        await foreach (var track in provider.EnumerateAsync(session, token)) { count++; last = track; }
        Program.Check(count == 129 && pages == 3 && last is { Extension: "flac", Length: 4096, Artist: "Artist" }, "Jellyfin catalog paging or metadata");
        await using var media = await provider.OpenAudioAsync(session, last!.Id, 1024, 1088, true, token);
        Program.Check(media.Message.StatusCode == HttpStatusCode.PartialContent, "Jellyfin Range was not forwarded");
        rejectLogin = true;
        try
        {
            await provider.AuthenticateAsync(Root, "test", "secret", token);
            throw new CheckException("Expected Jellyfin to reject the credentials.");
        }
        catch (WebDavException ex) when (ex.Code == "AuthenticationFailed" && ex.Status == HttpStatusCode.Unauthorized) { }
    }

    private static async Task VerifyNavidromeAsync(CancellationToken token)
    {
        var salts = new HashSet<string>();
        using var http = new RemoteMediaHttp(new FixtureHandler((request, _) =>
        {
            var uri = request.RequestUri!;
            Program.Check(uri.AbsolutePath.StartsWith("/reverse-proxy/rest/", StringComparison.Ordinal), "lost Navidrome route prefix");
            string salt = ParseQuery(uri, "s");
            string auth = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes("sëcret" + salt)));
            Program.Check(ParseQuery(uri, "u") == "test @+" && ParseQuery(uri, "t") == auth && salts.Add(salt) &&
                !uri.Query.Contains("p=", StringComparison.Ordinal), "Navidrome salted token or escaped username");
            var result = Path.GetFileName(uri.AbsolutePath) switch
            {
                "ping.view" => Subsonic("""{"status":"ok"}"""),
                "getAlbumList2.view" => Subsonic("""{"status":"ok","albumList2":{"album":[{"id":"a+/é"}]}}"""),
                "getAlbum.view" => ParseQuery(uri, "id") == "a+/é"
                    ? Subsonic("""{"status":"ok","album":{"id":"a+/é","songCount":1,"artist":"A","song":[{"id":"s?1","title":"Track","suffix":"mp3","size":4096}]}}""")
                    : throw new CheckException("Navidrome opaque album identity lost."),
                "stream.view" => ParseQuery(uri, "format") == "raw" && ParseQuery(uri, "id") == "s?1"
                    ? new(HttpStatusCode.Found) { Headers = { Location = new Uri("https://outside.test/") } }
                    : throw new CheckException("Navidrome original media request wrong."),
                _ => throw new CheckException("Unexpected fixture endpoint or forwarded redirect."),
            };
            return Task.FromResult(result);
        }));
        var provider = new NavidromeProvider(http);
        var session = await provider.AuthenticateAsync(Root, "test @+", "sëcret", token);
        await provider.ProbeAsync(session, token);
        var tracks = new List<CatalogTrack>();
        await foreach (var track in provider.EnumerateAsync(session, token)) tracks.Add(track);
        Program.Check(tracks is [{ Extension: "mp3", AlbumArtist: "A", Id: "s?1", Length: 4096 }], "Navidrome catalog mapping");
        try
        {
            await using var unexpected = await provider.OpenAudioAsync(session, tracks[0].Id, null, null, false, token);
            throw new CheckException("A credential-bearing Navidrome request followed a redirect.");
        }
        catch (WebDavException ex) when (ex.Code == "InvalidRedirect") { }
    }

    private static string ParseQuery(Uri uri, string name)
    {
        foreach (string argument in uri.Query[1..].Split('&'))
        {
            int separator = argument.IndexOf('=');
            if (separator > 0 && argument[..separator] == name) return Uri.UnescapeDataString(argument[(separator + 1)..]);
        }
        return "";
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Subsonic(string json) => Json("{\"subsonic-response\":" + json + "}");

    private sealed class FixtureHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
