using System.Net;
using System.Net.Http.Headers;
using WinUIMusicPlayer.Services.RemoteSources;
using WinUIMusicPlayer.Services.WebDav;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
            if (args.Length == 0) await Fixtures.RunAsync(deadline.Token);
            else await RunLiveAsync(args[0], deadline.Token);
            return 0;
        }
        catch (WebDavException ex)
        {
            Console.Error.WriteLine($"FAIL {ex.Code}, HTTP {(int?)ex.Status}");
            return 1;
        }
        catch (Exception ex)
        {
            // Network diagnostics may contain signed request URLs. Only fixture assertions include their messages.
            Console.Error.WriteLine(ex is CheckException ? ex.Message : $"FAIL {ex.GetType().Name}");
            return 1;
        }
    }

    private static async Task RunLiveAsync(string kind, CancellationToken token)
    {
        var root = WebDavTransport.NormalizeRoot(RequiredEnvironment("REMOTE_CATALOG_URL"));
        using var http = new RemoteMediaHttp();
        IRemoteMediaProvider provider = kind switch
        {
            "jellyfin" => new JellyfinProvider(http),
            "navidrome" => new NavidromeProvider(http),
            _ => throw new CheckException("Expected jellyfin or navidrome.")
        };
        var session = await provider.AuthenticateAsync(root, RequiredEnvironment("REMOTE_CATALOG_USER"),
            RequiredEnvironment("REMOTE_CATALOG_PASSWORD", allowEmpty: true), token);
        await provider.ProbeAsync(session, token);
        Console.WriteLine($"PASS {kind} authentication and authenticated catalog probe");

        List<CatalogTrack> tracks = [];
        await foreach (var track in provider.EnumerateAsync(session, token)) tracks.Add(track);
        Check(tracks.Count != 0, "expected at least one live track");
        Console.WriteLine($"PASS {kind} fully enumerated {tracks.Count} tracks");
        await CatalogDatabaseChecks.VerifyLiveSyncAsync(provider, session, tracks, token);

        var cover = tracks.FirstOrDefault(track => track.CoverId.Length != 0)
            ?? throw new CheckException("Expected a live track with cover art.");
        await using (var response = await provider.OpenCoverAsync(session, cover.CoverId, token))
        using (var image = await RemoteMediaHttp.ReadBodyAsync(response, 8 * 1024 * 1024, token))
            Check(image.Length > 0 && response.Message.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true,
                "expected image bytes from the live cover endpoint");
        Console.WriteLine($"PASS {kind} authenticated cover request");

        foreach (string extension in new[] { "flac", "mp3" })
        {
            var track = tracks.Find(item => item.Extension == extension && item.Length > 4096) ??
                tracks.Find(item => item.Extension == extension) ?? throw new CheckException($"Expected a live {extension} track.");
            long length = await CheckRangeAsync(provider, session, track, token);
            Console.WriteLine($"PASS {kind} raw {extension} byte ranges at start and midpoint ({length} bytes)");
            if (Environment.GetEnvironmentVariable("REMOTE_CATALOG_PLAYER_PATH") is { Length: > 0 })
                await LivePlaybackProbe.RunAsync(provider, session, track, token);
        }
    }

    private static async Task<long> CheckRangeAsync(IRemoteMediaProvider provider, CatalogSession session, CatalogTrack track, CancellationToken token)
    {
        long length;
        await using (var prefix = await provider.OpenAudioAsync(session, track.Id, 0, 1023, true, token))
        {
            length = CheckRangeHeaders(prefix.Message, 0, 1023);
            byte[] prefixBytes = new byte[1024];
            await prefix.Stream.ReadExactlyAsync(prefixBytes, token);
            if (track.Extension == "flac") Check(prefixBytes.AsSpan().StartsWith("fLaC"u8), "live FLAC was transcoded");
        }
        Check(length > 2048, "fixture track does not have a midpoint range");
        long start = length / 2;
        await using var middle = await provider.OpenAudioAsync(session, track.Id, start, start + 127, true, token);
        Check(CheckRangeHeaders(middle.Message, start, start + 127) == length, "live midpoint length changed");
        byte[] bytes = new byte[128];
        await middle.Stream.ReadExactlyAsync(bytes, token);
        return length;
    }

    private static long CheckRangeHeaders(HttpResponseMessage response, long start, long end)
    {
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        Check(response.StatusCode == HttpStatusCode.PartialContent && range?.From == start && range.To == end && range.Length > end,
            "live origin did not return the requested 206 byte range");
        return range!.Length!.Value;
    }

    private static string RequiredEnvironment(string name, bool allowEmpty = false)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (value is null || !allowEmpty && value.Length == 0) throw new CheckException($"{name} is required.");
        return value;
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new CheckException("FAIL " + message);
    }
}

internal sealed class CheckException(string message) : Exception(message);
