using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>凭据仅存在于请求生命周期；持久化由主程序的凭据库负责。</summary>
public sealed record WebDavConnection(Uri Root, string UserName, string Password)
{
    public override string ToString() => "WebDAV connection";
}
public sealed record WebDavEntry(string Href, string Name, bool IsDirectory, long Length, string ETag, DateTimeOffset? Modified)
{
    public string DisplayName => IsDirectory ? Name + "/" : Name;
}

/// <summary>对用户报告固定错误类型，避免异常消息泄漏重定向签名和认证信息。</summary>
public sealed class WebDavException(string code, HttpStatusCode? status = null) : IOException(code)
{
    public string Code { get; } = code;
    public HttpStatusCode? Status { get; } = status;
}

/// <summary>只读 DAV/HTTP 传输；响应持有并发租约直至释放，给前台保留请求容量。</summary>
public sealed class WebDavTransport : IDisposable
{
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _all = new(8, 8);
    private readonly SemaphoreSlim _background = new(6, 6);
    private static readonly XNamespace Dav = "DAV:";

    public WebDavTransport()
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 8
        }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static Uri NormalizeRoot(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new WebDavException("InvalidAddress");
        return uri.AbsolutePath.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    public static bool IsWithin(Uri root, Uri candidate) =>
        SameOrigin(root, candidate) && candidate.AbsolutePath.StartsWith(root.AbsolutePath, StringComparison.Ordinal);

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) && left.Port == right.Port;

    public static Uri Resolve(WebDavConnection source, string href)
    {
        if (!Uri.TryCreate(source.Root, href, out var uri) || !IsWithin(source.Root, uri) ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new WebDavException("InvalidResource");
        return uri;
    }

    public async IAsyncEnumerable<WebDavEntry> ListAsync(WebDavConnection source, string href,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var response = await SendAsync(source, href, "PROPFIND", null, null, false, deadline.Token).ConfigureAwait(false);
        if ((int)response.Message.StatusCode != 207) throw new WebDavException("InvalidDirectoryResponse", response.Message.StatusCode);
        using var cancelRead = deadline.Token.Register(static stream => ((Stream)stream!).Dispose(), response.Stream);
        using var reader = XmlReader.Create(response.Stream, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = 64L * 1024 * 1024, IgnoreWhitespace = true
        });
        var requested = Resolve(source, href);
        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "response" || reader.NamespaceURI != "DAV:") continue;
            using var subtree = reader.ReadSubtree();
            var element = await XElement.LoadAsync(subtree, LoadOptions.None, deadline.Token).ConfigureAwait(false);
            string? rawHref = (string?)element.Element(Dav + "href");
            if (string.IsNullOrEmpty(rawHref)) throw new WebDavException("InvalidDirectoryResponse");
            var resource = Resolve(source, rawHref);
            if (resource.AbsolutePath.TrimEnd('/') == requested.AbsolutePath.TrimEnd('/')) continue;
            // Depth:1 的响应必须是直接子项，不能让异常服务器逃出用户选定目录。
            if (!resource.AbsolutePath.StartsWith(requested.AbsolutePath, StringComparison.Ordinal))
                throw new WebDavException("InvalidDirectoryResponse");
            string relative = resource.AbsolutePath[requested.AbsolutePath.Length..].TrimEnd('/');
            if (relative.Contains('/')) throw new WebDavException("InvalidDirectoryResponse");
            bool? directory = null;
            long length = -1;
            string etag = "";
            DateTimeOffset? modified = null;
            foreach (var propstat in element.Elements(Dav + "propstat"))
            {
                string status = (string?)propstat.Element(Dav + "status") ?? "";
                if (!status.Contains(" 200 ", StringComparison.Ordinal)) continue;
                var prop = propstat.Element(Dav + "prop");
                if (prop is null) continue;
                if (prop.Element(Dav + "resourcetype") is { } type) directory = type.Element(Dav + "collection") is not null;
                if (long.TryParse((string?)prop.Element(Dav + "getcontentlength"), CultureInfo.InvariantCulture, out var size)) length = size;
                etag = (string?)prop.Element(Dav + "getetag") ?? etag;
                if (DateTimeOffset.TryParse((string?)prop.Element(Dav + "getlastmodified"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)) modified = date;
            }
            if (directory is null) throw new WebDavException("IncompleteDirectoryResponse");
            string path = directory.Value ? resource.AbsolutePath.TrimEnd('/') + "/" : resource.AbsolutePath;
            yield return new(path, Uri.UnescapeDataString(relative), directory.Value, length, etag, modified);
        }
    }

    public Task<WebDavResponse> OpenAsync(WebDavConnection source, string href, long? start, long? end,
        bool foreground, CancellationToken token) => SendAsync(source, href, "GET", start, end, foreground, token);

    private async Task<WebDavResponse> SendAsync(WebDavConnection source, string href, string method,
        long? start, long? end, bool foreground, CancellationToken token)
    {
        bool backgroundHeld = false, allHeld = false;
        try
        {
            if (!foreground) { await _background.WaitAsync(token).ConfigureAwait(false); backgroundHeld = true; }
            await _all.WaitAsync(token).ConfigureAwait(false);
            allHeld = true;
            var target = Resolve(source, href);
            using var headersDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            headersDeadline.CancelAfter(TimeSpan.FromSeconds(20));
            for (int redirect = 0; redirect <= 5; redirect++)
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), target);
                request.Headers.AcceptEncoding.ParseAdd("identity");
                if (IsWithin(source.Root, target) && source.UserName.Length != 0)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(source.UserName + ":" + source.Password)));
                if (start is not null) request.Headers.Range = new RangeHeaderValue(start, end);
                if (method == "PROPFIND")
                {
                    request.Headers.Add("Depth", "1");
                    request.Content = new StringContent("<d:propfind xmlns:d=\"DAV:\"><d:prop><d:resourcetype/><d:getcontentlength/><d:getetag/><d:getlastmodified/></d:prop></d:propfind>", Encoding.UTF8, "application/xml");
                }
                var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersDeadline.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var location = response.Headers.Location;
                    response.Dispose();
                    if (location is null || redirect == 5) throw new WebDavException("InvalidRedirect");
                    var next = new Uri(target, location);
                    if (next.Scheme is not ("http" or "https") || next.UserInfo.Length != 0 ||
                        (target.Scheme == "https" && next.Scheme != "https") || (method == "PROPFIND" && !IsWithin(source.Root, next)))
                        throw new WebDavException("InvalidRedirect");
                    target = next;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                {
                    var status = response.StatusCode;
                    response.Dispose();
                    throw new WebDavException(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "AuthenticationFailed" : "RequestFailed", status);
                }
                try
                {
                    if (response.Content.Headers.ContentEncoding.Count != 0)
                        throw new WebDavException("UnexpectedContentEncoding");
                    var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    return new WebDavResponse(response, stream, _all, backgroundHeld ? _background : null);
                }
                catch { response.Dispose(); throw; }
            }
            throw new WebDavException("InvalidRedirect");
        }
        catch
        {
            if (allHeld) _all.Release();
            if (backgroundHeld) _background.Release();
            throw;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>响应与并发名额具有相同生命周期；调用者必须等待读取退出后再释放。</summary>
public sealed class WebDavResponse(HttpResponseMessage message, Stream stream, SemaphoreSlim all, SemaphoreSlim? background) : IAsyncDisposable
{
    private int _disposed;
    public HttpResponseMessage Message { get; } = message;
    public Stream Stream { get; } = stream;
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Message.Dispose();
            all.Release();
            background?.Release();
        }
        return ValueTask.CompletedTask;
    }
}
