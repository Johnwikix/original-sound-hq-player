using System;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>每次读取独立跟踪下载版本；播放窗口并发校验时共用同一个快照。</summary>
internal sealed class RemoteResourceVersion(WebDavEntry entry)
{
    private readonly object _gate = new();
    private bool _initialized, _redirected;
    private string? _etag;
    private DateTimeOffset? _modified;

    /// <returns>是否能够把字节持久化到以 DAV 强 ETag 为键的缓存。</returns>
    public bool Validate(WebDavResponse response)
    {
        string? etag = response.Message.Headers.ETag?.ToString();
        var modified = response.Message.Content.Headers.LastModified;
        bool sourceIsStrong = entry.ETag.Length != 0 && !entry.ETag.StartsWith("W/", StringComparison.Ordinal);
        if (!response.IsRedirected && sourceIsStrong && etag is not null && etag != entry.ETag)
            throw new WebDavException("ResourceChanged");
        lock (_gate)
        {
            if (_initialized && (_redirected != response.IsRedirected || _etag != etag || _modified != modified))
                throw new WebDavException("ResourceChanged");
            _initialized = true;
            _redirected = response.IsRedirected;
            _etag = etag;
            _modified = modified;
        }
        // DAV/直链版本不同或下载缺少版本时只使用内存窗口；相同强版本保留原有持久缓存能力。
        return sourceIsStrong && etag == entry.ETag;
    }
}
