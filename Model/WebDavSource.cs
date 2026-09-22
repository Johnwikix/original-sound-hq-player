using SQLite;
using System;

namespace WinUIMusicPlayer.Model;

/// <summary>远程来源的持久配置。密码单独保存在 Windows 凭据库。</summary>
public sealed class WebDavSource
{
    [PrimaryKey, AutoIncrement] public int Id { get; set; }
    public string Name { get; set; } = "";
    public string BaseUri { get; set; } = "";
    public string UserName { get; set; } = "";
    public string CredentialKey { get; set; } = Guid.NewGuid().ToString("N");
    public string Roots { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool ScanOnStartup { get; set; }
    public bool ReadMetadata { get; set; } = true;
    public DateTime LastScanUtc { get; set; }
}

/// <summary>稳定 DAV 文件身份；从不持久化签名 URL 或请求头。</summary>
public sealed class RemoteTrack
{
    [PrimaryKey] public int MusicId { get; set; }
    [Indexed] public int SourceId { get; set; }
    public string Href { get; set; } = "";
    public string ParentHref { get; set; } = "";
    public long Length { get; set; } = -1;
    public string ETag { get; set; } = "";
    public string Modified { get; set; } = "";
    public string MetadataState { get; set; } = "Pending";
    public string SeenRun { get; set; } = "";
    public bool Missing { get; set; }
}

/// <summary>音频缓存独立于封面缓存；缺省关闭。</summary>
public sealed class WebDavCacheSettings
{
    [PrimaryKey] public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public string Directory { get; set; } = "";
    public int LimitGiB { get; set; } = 10;
}
