using System;
using System.Net.Http;
using System.IO;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>只在源端读取边界生成；播放器关闭 loopback 请求不代表 WebDAV 离线。</summary>
public sealed record WebDavReadFailure(string Code, bool SourceUnavailable)
{
    public static WebDavReadFailure From(Exception error) => error switch
    {
        WebDavException dav => new(dav.Code, dav.Code is "AuthenticationFailed" or "AuthenticationRequired" or
            "TlsConnectionFailed" or "CertificateUntrusted" or "CertificateChanged" or "ConnectionFailed" ||
            dav.Status is { } status && (int)status >= 500),
        HttpRequestException or IOException or OperationCanceledException => new("ConnectionFailed", true),
        _ => new("PlaybackFailed", false)
    };
}
