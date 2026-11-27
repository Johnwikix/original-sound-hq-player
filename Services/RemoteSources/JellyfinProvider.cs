using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services.RemoteSources;

internal sealed class JellyfinProvider(RemoteMediaHttp http) : IRemoteMediaProvider
{
    private const int PageSize = 128;
    public RemoteSourceKind Kind => RemoteSourceKind.Jellyfin;

    public async Task<CatalogSession> AuthenticateAsync(Uri root, string userName, string password, CancellationToken token,
        string? deviceId = null)
    {
        string device = Guid.TryParse(deviceId, out Guid id) ? id.ToString("N") : Guid.NewGuid().ToString("N");
        string authorization = $"MediaBrowser Client=\"Original Sound Player\", Device=\"Windows\", DeviceId=\"{device}\", Version=\"1.0.0\"";
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new JellyfinLogin(userName, password), JellyfinJsonContext.Default.JellyfinLogin);
        try
        {
            // 登录直接使用已有的 HTTP 内容类型，避免在自包含包中额外解析 System.Net.Http.Json。
            var request = Request(root, "Users/AuthenticateByName", authorization, HttpMethod.Post);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
            using var document = await http.ReadJsonAsync(request, token).ConfigureAwait(false);
            string accessToken = document.RootElement.Text("AccessToken");
            string userId = document.RootElement.Property("User").Text("Id");
            if (accessToken.Length == 0 || accessToken.AsSpan().IndexOfAny('"', '\r', '\n') >= 0 || !Guid.TryParse(userId, out Guid account))
                throw new WebDavException("AuthenticationFailed");
            return new(root, userName, "", account.ToString("N"), authorization + $", Token=\"{accessToken}\"");
        }
        finally { CryptographicOperations.ZeroMemory(body); }
    }

    public async Task ProbeAsync(CatalogSession session, CancellationToken token)
    {
        using var document = await http.ReadJsonAsync(ItemsRequest(session, 0, 1), token).ConfigureAwait(false);
        _ = Items(document);
    }

    public async IAsyncEnumerable<CatalogTrack> EnumerateAsync(CatalogSession session, [EnumeratorCancellation] CancellationToken token)
    {
        int total = -1;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; ; )
        {
            using var document = await http.ReadJsonAsync(ItemsRequest(session, offset, PageSize), token).ConfigureAwait(false);
            var items = Items(document);
            int count = items.GetArrayLength();
            long reported = document.RootElement.Long("TotalRecordCount", -1);
            if (reported < 0 || reported > int.MaxValue || (total >= 0 && reported != total) || count > PageSize)
                throw new WebDavException("InvalidDirectoryResponse");
            total = (int)reported;
            if (count > total - offset || (count == 0 && offset < total))
                throw new WebDavException("InvalidDirectoryResponse");
            foreach (var item in items.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                var track = ParseTrack(item);
                if (!seen.Add(track.Id)) throw new WebDavException("InvalidDirectoryResponse");
                yield return track;
            }
            offset += count;
            if (offset == total) yield break;
        }
    }

    public Task<WebDavResponse> OpenAudioAsync(CatalogSession session, string id, long? start, long? end, bool foreground,
        CancellationToken token) => http.SendAsync(Request(session.Root, $"Audio/{JellyfinId(id)}/stream?static=true",
            session.Authorization), start, end, foreground, token);

    public Task<WebDavResponse> OpenCoverAsync(CatalogSession session, string id, CancellationToken token) =>
        http.SendAsync(Request(session.Root, $"Items/{JellyfinId(id)}/Images/Primary?maxWidth=768&maxHeight=768",
            session.Authorization), null, null, false, token);

    private static HttpRequestMessage ItemsRequest(CatalogSession session, int offset, int limit) =>
        Request(session.Root, $"Items?userId={session.AccountId}&recursive=true&includeItemTypes=Audio&enableUserData=false" +
            $"&enableTotalRecordCount=true&sortBy=SortName&startIndex={CatalogJson.Number(offset)}&limit={CatalogJson.Number(limit)}" +
            "&fields=MediaSources,MediaStreams,Genres", session.Authorization);

    private static JsonElement Items(JsonDocument document)
    {
        var items = document.RootElement.Property("Items");
        if (items.ValueKind != JsonValueKind.Array) throw new WebDavException("InvalidDirectoryResponse");
        return items;
    }

    private static HttpRequestMessage Request(Uri root, string path, string authorization, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Get, new Uri(root, path));
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private static string JellyfinId(string id) => Guid.TryParse(id, out Guid guid)
        ? guid.ToString("N") : throw new WebDavException("InvalidDirectoryResponse");

    private static CatalogTrack ParseTrack(JsonElement item)
    {
        string id = JellyfinId(item.RequiredId("Id"));
        string albumId = item.Text("AlbumId");
        if (albumId.Length != 0) albumId = JellyfinId(albumId);
        string coverVersion = item.Property("ImageTags").Text("Primary");
        string coverId = coverVersion.Length != 0 ? id : "";
        if (coverId.Length == 0 && albumId.Length != 0)
        {
            coverId = albumId;
            coverVersion = item.Text("AlbumPrimaryImageTag");
        }

        var sources = item.Property("MediaSources");
        var media = sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0 ? sources[0] : default;
        var streamList = media.Property("MediaStreams");
        if (streamList.ValueKind == JsonValueKind.Undefined) streamList = item.Property("MediaStreams");
        JsonElement audio = default;
        if (streamList.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streamList.EnumerateArray())
            {
                if (stream.Text("Type") != "Audio") continue;
                audio = stream;
                break;
            }
        }
        string extension = CatalogJson.Extension(media.Text("Container"));
        if (extension.Length == 0) extension = CatalogJson.Extension(item.Text("Container"));
        long length = media.Long("Size", -1);
        string albumArtist = item.Text("AlbumArtist");
        if (albumArtist.Length == 0) albumArtist = item.Names("AlbumArtists");
        return new(id, item.Text("Name"), item.Names("Artists"), item.Text("Album"), albumId,
            albumArtist, item.Names("Genres"), item.Int("IndexNumber"), item.Int("ParentIndexNumber"),
            item.Int("ProductionYear"), TimeSpan.FromTicks(Math.Max(0L, item.Long("RunTimeTicks"))),
            extension, length > 0 ? length : -1, coverId, coverVersion, audio.Int("BitRate") / 1000,
            audio.Int("SampleRate"), audio.Int("Channels"), audio.Int("BitDepth"));
    }
}

internal sealed record JellyfinLogin([property: JsonPropertyName("Username")] string UserName,
    [property: JsonPropertyName("Pw")] string Password);

[JsonSerializable(typeof(JellyfinLogin))]
internal sealed partial class JellyfinJsonContext : JsonSerializerContext;
