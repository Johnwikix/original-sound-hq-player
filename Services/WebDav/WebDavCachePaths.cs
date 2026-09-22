using System.IO;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>WebDAV 与本地封面共用用户配置的缓存根目录；下载和原图分别管理。</summary>
public static class WebDavCachePaths
{
    public static string Root(string cacheRoot) => Path.Combine(Path.GetFullPath(cacheRoot), "WebDav");
    public static string Audio(string cacheRoot) => Path.Combine(Root(cacheRoot), "Audio");
    public static string Covers(string cacheRoot) => Path.Combine(Root(cacheRoot), "Covers");
    public static string Cover(string cacheRoot, string imageHash) => Path.Combine(Covers(cacheRoot), imageHash + "_raw.bin");

    /// <summary>展示控件只有封面标识，统一查找远程原图与既有本地原图，不复制第二份文件。</summary>
    public static string FindCover(string cacheRoot, string imageHash)
    {
        string remote = Cover(cacheRoot, imageHash);
        return File.Exists(remote) ? remote : Path.Combine(cacheRoot, "Cache", imageHash + "_raw.bin");
    }
}
