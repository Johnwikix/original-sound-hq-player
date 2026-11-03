using System;
using System.IO;

namespace WinUIMusicPlayer.Helper;

/// <summary>统计当前缓存位置中的应用缓存；不把用户同目录的音乐等文件计入。</summary>
internal static class CacheSizeCalculator
{
    internal static long GetSize(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return 0;
        // 根目录的网络封面，以及封面/WebDAV 专用目录中的所有格式和未完成文件。
        return SumFiles(root, "*.bin", false) +
            SumFiles(Path.Combine(root, "Cache"), "*", true) +
            SumFiles(Path.Combine(root, "WebDav"), "*", true) +
            SumFiles(Path.Combine(root, "Analysis"), "*", true);
    }

    private static long SumFiles(string directory, string pattern, bool recursive)
    {
        long size = 0;
        try
        {
            var folder = new DirectoryInfo(directory);
            // 不跟随目录链接，避免重复统计或进入缓存范围之外。
            if (recursive && folder.Exists && (folder.Attributes & FileAttributes.ReparsePoint) != 0)
                return 0;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            foreach (var file in folder.EnumerateFiles(pattern, options))
            {
                try { size += file.Length; }
                catch (FileNotFoundException) { } // 并发裁剪/原子发布不影响其他文件统计。
                catch (DirectoryNotFoundException) { }
            }
        }
        catch (DirectoryNotFoundException) { } // 尚未创建或刚被清空的缓存目录。
        return size;
    }

    internal static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / (1024d * 1024 * 1024):F2} GiB",
        >= 1024L * 1024 => $"{bytes / (1024d * 1024):F2} MiB",
        >= 1024 => $"{bytes / 1024d:F2} KiB",
        _ => $"{bytes} B"
    };
}
