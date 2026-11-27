using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services.RemoteSources;

internal sealed class NavidromeProvider(RemoteMediaHttp http) : IRemoteMediaProvider
{
    private const int PageSize = 100;
    private const string Client = "OriginalSoundPlayer";
    public RemoteSourceKind Kind => RemoteSourceKind.Navidrome;

    public Task<CatalogSession> AuthenticateAsync(Uri root, string userName, string password, CancellationToken token, string? deviceId = null) =>
        Task.FromResult(new CatalogSession(root, userName, password));

    public async Task ProbeAsync(CatalogSession session, CancellationToken token)
    {
        using var document = await http.ReadJsonAsync(Request(session, "ping", ""), token).ConfigureAwait(false);
        _ = Response(document);
    }

    public async IAsyncEnumerable<CatalogTrack> EnumerateAsync(CatalogSession session, [EnumeratorCancellation] CancellationToken token)
    {
        var seenAlbums = new HashSet<string>(StringComparer.Ordinal);
        var seenSongs = new HashSet<string>(StringComparer.Ordinal);
        for (int offset = 0; ; )
        {
            using var document = await http.ReadJsonAsync(Request(session, "getAlbumList2",
                $"&type=alphabeticalByName&size={CatalogJson.Number(PageSize)}&offset={CatalogJson.Number(offset)}"),
                token).ConfigureAwait(false);
            var list = Response(document).Property("albumList2");
            var albums = list.Property("album");
            if (list.ValueKind != JsonValueKind.Object || albums.ValueKind is not (JsonValueKind.Array or JsonValueKind.Undefined))
                throw new WebDavException("InvalidDirectoryResponse");
            int count = albums.ValueKind == JsonValueKind.Array ? albums.GetArrayLength() : 0;
            if (count > PageSize) throw new WebDavException("InvalidDirectoryResponse");
            if (count == 0) yield break;
            foreach (var entry in albums.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                string albumId = entry.RequiredId("id");
                if (!seenAlbums.Add(albumId)) throw new WebDavException("InvalidDirectoryResponse");
                using var albumDocument = await http.ReadJsonAsync(Request(session, "getAlbum",
                    "&id=" + CatalogJson.Escape(albumId)), token).ConfigureAwait(false);
                var album = Response(albumDocument).Property("album");
                if (album.Text("id") != albumId) throw new WebDavException("InvalidDirectoryResponse");
                var songs = album.Property("song");
                if (songs.ValueKind != JsonValueKind.Array)
                {
                    if (songs.ValueKind == JsonValueKind.Undefined && album.Long("songCount", -1) == 0) continue;
                    throw new WebDavException("InvalidDirectoryResponse");
                }
                if (album.Long("songCount", songs.GetArrayLength()) != songs.GetArrayLength())
                    throw new WebDavException("InvalidDirectoryResponse");
                foreach (var item in songs.EnumerateArray())
                {
                    var track = ParseTrack(item, album, albumId);
                    if (!seenSongs.Add(track.Id)) throw new WebDavException("InvalidDirectoryResponse");
                    yield return track;
                }
            }
            if (count < PageSize) yield break;
            offset = checked(offset + count);
        }
    }

    public Task<WebDavResponse> OpenAudioAsync(CatalogSession session, string id, long? start, long? end, bool foreground,
        CancellationToken token) => OpenMediaAsync(Request(session, "stream", "&format=raw&id=" + CatalogJson.Escape(id)),
            start, end, foreground, token);

    public Task<WebDavResponse> OpenCoverAsync(CatalogSession session, string id, CancellationToken token) =>
        OpenMediaAsync(Request(session, "getCoverArt", "&size=768&id=" + CatalogJson.Escape(id)), null, null, false, token);

    private async Task<WebDavResponse> OpenMediaAsync(HttpRequestMessage request, long? start, long? end, bool foreground,
        CancellationToken token)
    {
        var response = await http.SendAsync(request, start, end, foreground, token).ConfigureAwait(false);
        // Subsonic errors may be returned as an API envelope with HTTP 200.
        string? mediaType = response.Message.Content.Headers.ContentType?.MediaType;
        if (mediaType is not ("application/json" or "text/json" or "application/xml" or "text/xml"))
            return response;
        await using (response)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            if (mediaType is "application/json" or "text/json")
            {
                using var body = await RemoteMediaHttp.ReadBodyAsync(response, 16 * 1024, deadline.Token).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(body, cancellationToken: deadline.Token).ConfigureAwait(false);
                _ = Response(document);
            }
            throw new WebDavException("RequestFailed");
        }
    }

    private static HttpRequestMessage Request(CatalogSession session, string method, string parameters)
    {
        Span<byte> random = stackalloc byte[12];
        RandomNumberGenerator.Fill(random);
        string salt = Convert.ToHexStringLower(random);
        string token = CreateToken(session.Secret, salt);
        var uri = new Uri(session.Root, $"rest/{method}.view?v=1.16.1&c={Client}&f=json&u={CatalogJson.Escape(session.UserName)}&t={token}&s={salt}{parameters}");
        return new HttpRequestMessage(HttpMethod.Get, uri);
    }

    // Subsonic specifies MD5(UTF8(password + salt)) for its challenge token.
    private static string CreateToken(ReadOnlySpan<char> password, ReadOnlySpan<char> salt)
    {
        int size = Encoding.UTF8.GetByteCount(password) + Encoding.UTF8.GetByteCount(salt);
        byte[]? rental = null;
        Span<byte> bytes = size <= 512 ? stackalloc byte[512] : (rental = ArrayPool<byte>.Shared.Rent(size));
        try
        {
            int length = Encoding.UTF8.GetBytes(password, bytes);
            length += Encoding.UTF8.GetBytes(salt, bytes[length..]);
            Span<byte> digest = stackalloc byte[16];
            MD5.HashData(bytes[..length], digest);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes[..size]);
            if (rental is not null) ArrayPool<byte>.Shared.Return(rental);
        }
    }

    private static JsonElement Response(JsonDocument document)
    {
        var response = document.RootElement.Property("subsonic-response");
        if (response.Text("status") == "ok") return response;
        throw new WebDavException(response.Property("error").Int("code") switch
        {
            40 or 41 or 42 or 43 or 44 or 50 => "AuthenticationFailed",
            70 => "ResourceMissing",
            _ => "InvalidDirectoryResponse"
        });
    }

    private static CatalogTrack ParseTrack(JsonElement item, JsonElement album, string albumId)
    {
        string coverId = item.Text("coverArt");
        if (coverId.Length == 0) coverId = album.Text("coverArt");
        string title = item.Text("album");
        if (title.Length == 0) title = album.Text("name");
        long length = item.Long("size", -1);
        return new(item.RequiredId("id"), item.Text("title"), item.Text("artist"), title, albumId, album.Text("artist"),
            item.Text("genre"), item.Int("track"), item.Int("discNumber"), item.Int("year"),
            TimeSpan.FromSeconds(item.Int("duration")), CatalogJson.Extension(item.Text("suffix")),
            length > 0 ? length : -1, coverId, "", item.Int("bitRate"), item.Int("samplingRate"), item.Int("channelCount"),
            item.Int("bitDepth"));
    }
}
