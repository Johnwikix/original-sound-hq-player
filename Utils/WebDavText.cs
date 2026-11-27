using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils;

/// <summary>将固定传输错误映射到独立资源键，避免显示内部异常或原始键名。</summary>
public static class WebDavText
{
    public static string SourceName(RemoteSourceKind kind) => kind switch
    {
        RemoteSourceKind.Jellyfin => "Jellyfin",
        RemoteSourceKind.Navidrome => "Navidrome",
        _ => "WebDAV"
    };

    public static string Error(string code) => ToolUtils.GetString(code switch
    {
        "AuthenticationRequired" or "AuthenticationFailed" => "WebDavAuthenticationError",
        "CertificateUntrusted" => "WebDavCertificateUntrusted",
        "CertificateChanged" => "WebDavCertificateChanged",
        "TlsConnectionFailed" => "WebDavTlsError",
        "InvalidAddress" or "InvalidResource" or "InvalidRedirect" => "WebDavAddressError",
        "NameRequired" => "MusicSourcesNameRequired",
        "NameExists" => "WebDavNameError",
        "ResourceMissing" or "SourceUnavailable" => "WebDavMissingError",
        "ResourceChanged" => "WebDavChangedError",
        "RangeNotSupported" => "WebDavRangeError",
        "Cancelled" => "WebDavCancelled",
        _ => "WebDavErrorConnectionFailed"
    });
}
