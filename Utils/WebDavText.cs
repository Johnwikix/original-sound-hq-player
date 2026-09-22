namespace WinUIMusicPlayer.Utils;

/// <summary>将固定传输错误映射到独立资源键，避免显示内部异常或原始键名。</summary>
public static class WebDavText
{
    public static string Error(string code) => ToolUtils.GetString(code switch
    {
        "AuthenticationRequired" or "AuthenticationFailed" => "WebDavAuthenticationError",
        "InvalidAddress" or "InvalidResource" or "InvalidRedirect" => "WebDavAddressError",
        "NameRequired" or "NameExists" => "WebDavNameError",
        "ResourceMissing" or "SourceUnavailable" => "WebDavMissingError",
        "ResourceChanged" => "WebDavChangedError",
        "RangeNotSupported" => "WebDavRangeError",
        "Cancelled" => "WebDavCancelled",
        _ => "WebDavErrorConnectionFailed"
    });
}
