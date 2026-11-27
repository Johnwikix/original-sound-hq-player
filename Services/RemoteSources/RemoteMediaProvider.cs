using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services.RemoteSources;

internal sealed record CatalogTrack(string Id, string Title, string Artist, string Album, string AlbumId, string AlbumArtist,
    string Genre, int TrackNumber, int DiscNumber, int Year, TimeSpan Duration, string Extension, long Length,
    string CoverId, string CoverVersion, int BitRate = 0, int SampleRate = 0, int Channels = 0, int BitDepth = 0);

internal sealed record CatalogSession(Uri Root, string UserName, string Secret, string AccountId = "", string Authorization = "")
{
    public override string ToString() => "Music catalog connection";
}

internal interface IRemoteMediaProvider
{
    RemoteSourceKind Kind { get; }
    Task<CatalogSession> AuthenticateAsync(Uri root, string userName, string password, CancellationToken token, string? deviceId = null);
    Task ProbeAsync(CatalogSession session, CancellationToken token);
    IAsyncEnumerable<CatalogTrack> EnumerateAsync(CatalogSession session, CancellationToken token);
    Task<WebDavResponse> OpenAudioAsync(CatalogSession session, string id, long? start, long? end, bool foreground, CancellationToken token);
    Task<WebDavResponse> OpenCoverAsync(CatalogSession session, string id, CancellationToken token);
}

internal sealed class RemoteMediaHttp : IDisposable
{
    // Catalog work cannot consume the connections reserved for playback. Responses own their slot.
    private readonly SemaphoreSlim _all = new(8, 8);
    private readonly SemaphoreSlim _background = new(6, 6);
    private readonly HttpClient _client;

    internal RemoteMediaHttp(HttpMessageHandler? handler = null) => _client = new HttpClient(handler ?? new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<JsonDocument> ReadJsonAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var response = await SendAsync(request, null, null, false, deadline.Token).ConfigureAwait(false);
        try
        {
            // Provider requests use bounded pages. Reject unexpectedly large or invalid API replies.
            using var content = await ReadBodyAsync(response, 8 * 1024 * 1024, deadline.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(content, cancellationToken: deadline.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException)
        {
            throw new WebDavException("InvalidDirectoryResponse");
        }
    }

    public static async Task<MemoryStream> ReadBodyAsync(WebDavResponse response, int limit, CancellationToken token)
    {
        long length = response.Message.Content.Headers.ContentLength ?? 0;
        if (length > limit) throw new WebDavException("InvalidDirectoryResponse");
        var content = new MemoryStream((int)length);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                int count = await response.Stream.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (count == 0)
                {
                    content.Position = 0;
                    return content;
                }
                if (content.Length + count > limit) throw new WebDavException("InvalidDirectoryResponse");
                content.Write(buffer.AsSpan(0, count));
            }
        }
        catch
        {
            content.Dispose();
            throw;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }

    public async Task<WebDavResponse> SendAsync(HttpRequestMessage request, long? start, long? end, bool foreground,
        CancellationToken token)
    {
        using (request)
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            bool background = false;
            bool held = false;
            HttpResponseMessage? response = null;
            try
            {
                if (!foreground)
                {
                    await _background.WaitAsync(deadline.Token).ConfigureAwait(false);
                    background = true;
                }
                await _all.WaitAsync(deadline.Token).ConfigureAwait(false);
                held = true;
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
                response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                var status = response.StatusCode;
                if (!response.IsSuccessStatusCode)
                    throw new WebDavException(status switch
                    {
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "AuthenticationFailed",
                        HttpStatusCode.NotFound => "ResourceMissing",
                        >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest => "InvalidRedirect",
                        _ => "RequestFailed"
                    }, status);
                foreach (string encoding in response.Content.Headers.ContentEncoding)
                    if (!encoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
                        throw new WebDavException("UnexpectedContentEncoding");
                var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
                var result = new WebDavResponse(response, stream, _all, background ? _background : null);
                response = null;
                held = background = false;
                return result;
            }
            catch (HttpRequestException ex)
            {
                throw new WebDavException(ex.HttpRequestError == HttpRequestError.SecureConnectionError
                    ? "TlsConnectionFailed" : "ConnectionFailed", ex.StatusCode);
            }
            finally
            {
                response?.Dispose();
                if (held) _all.Release();
                if (background) _background.Release();
            }
        }
    }

    public void Dispose() => _client.Dispose();
}

internal static class CatalogJson
{
    public static JsonElement Property(this JsonElement item, string name) =>
        item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value : default;

    public static string Text(this JsonElement item, string name) =>
        item.Property(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? "" : "";

    public static long Long(this JsonElement item, string name, long fallback = 0) =>
        item.Property(name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt64(out long number) ? number : fallback;

    public static int Int(this JsonElement item, string name) => (int)Math.Clamp(item.Long(name), 0, int.MaxValue);

    public static string Names(this JsonElement item, string name)
    {
        if (item.Property(name) is not { ValueKind: JsonValueKind.Array } values) return "";
        var result = new List<string>(values.GetArrayLength());
        foreach (var element in values.EnumerateArray())
        {
            string? value = element.ValueKind == JsonValueKind.String ? element.GetString() : element.Text("Name");
            if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
        }
        return string.Join("; ", result);
    }

    public static string RequiredId(this JsonElement item, string name)
    {
        string value = item.Text(name);
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            throw new WebDavException("InvalidDirectoryResponse");
        return value;
    }

    public static string Extension(string extension)
    {
        int end = extension.AsSpan().IndexOf(',');
        if (end >= 0) extension = extension[..end];
        return extension.TrimStart('.').ToLowerInvariant();
    }

    public static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string Escape(string value) => Uri.EscapeDataString(value);
}
