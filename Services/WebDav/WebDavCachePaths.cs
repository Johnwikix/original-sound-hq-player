using System;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>WebDAV 与本地封面共用用户配置的缓存根目录；下载和原图分别管理。</summary>
public static class WebDavCachePaths
{
    public static string Root(string cacheRoot) => Path.Combine(Path.GetFullPath(cacheRoot), "WebDav");
    public static string Audio(string cacheRoot) => Path.Combine(Root(cacheRoot), "Audio");
    public static string Covers(string cacheRoot) => Path.Combine(Root(cacheRoot), "Covers");
    public static string Cover(string cacheRoot, string imageHash) => Path.Combine(Covers(cacheRoot), GetServerCoverKey(imageHash) + "_raw.bin");
    internal static string MatchedCover(string cacheRoot, string imageHash) => Path.Combine(Covers(cacheRoot), imageHash + "_matched_raw.bin");
    internal static string MissingCover(string cacheRoot, string imageHash) => Path.Combine(Covers(cacheRoot), GetServerCoverKey(imageHash) + "_missing_raw.bin");

    // The per-song key keeps matched images and thumbnails isolated. The optional final digest still locates the shared server image.
    internal static string GetJellyfinImageHash(int sourceId, string trackId, string serverKey, string title, string artist, string album)
    {
        string identity = string.Create(CultureInfo.InvariantCulture,
            $"jellyfin\n{sourceId}\n{trackId}\n{serverKey}\n{title.Length}:{title}{artist.Length}:{artist}{album}");
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(identity), digest);
        string key = "jf-" + Convert.ToHexString(digest[..16]);
        return serverKey.Length == 64 ? key + "-" + serverKey : key;
    }

    internal static string GetServerCoverKey(string imageHash) =>
        imageHash.Length == 100 && imageHash.StartsWith("jf-", StringComparison.Ordinal) && imageHash[35] == '-'
            ? imageHash[36..]
            : imageHash;

    /// <summary>展示控件只有封面标识，统一查找远程原图与既有本地原图，不复制第二份文件。</summary>
    public static string FindCover(string cacheRoot, string imageHash)
    {
        string remote = Cover(cacheRoot, imageHash);
        string matched = MatchedCover(cacheRoot, imageHash);
        if (imageHash.StartsWith("jf-", StringComparison.Ordinal) && File.Exists(matched))
        {
            var original = new FileInfo(remote);
            return original.Exists && original.Length > 0 ? remote : matched;
        }
        return File.Exists(remote) ? remote : Path.Combine(cacheRoot, "Cache", imageHash + "_raw.bin");
    }
}
