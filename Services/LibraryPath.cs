using System;
using System.IO;

namespace WinUIMusicPlayer.Services;

internal static class LibraryPath
{
    // Skipped reparse-point trees and temporarily inaccessible paths are not deletion evidence.
    // Unexpected IO/access errors propagate so the caller can skip the whole deletion phase.
    public static bool IsConfirmedMissing(string path, string root)
    {
        try
        {
            File.GetAttributes(path);
            return false;
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }

        string? parent = Path.GetDirectoryName(path);
        while (parent is not null && IsWithin(parent, root))
        {
            try
            {
                if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return false;
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
            parent = Path.GetDirectoryName(parent);
        }
        return true;
    }

    public static bool IsWithin(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder.AsSpan());
        return path.AsSpan().Equals(root, StringComparison.OrdinalIgnoreCase)
            || (path.AsSpan().StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path.Length > root.Length
                && (Path.EndsInDirectorySeparator(root) || (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar)));
    }

    /// <summary>
    /// 是否固定本地盘路径（外部打开文件的入库条件）。可移动盘/网络盘/UNC/光驱/无法判定
    /// 一律返回 false——这些位置不沉淀曲库（与 UsbDeviceMusic 的设备音乐体系保持边界），
    /// 走一次性播放。DriveInfo 构造与 DriveType 均为元数据查询，不触发介质唤醒或 I/O 等待。
    /// </summary>
    public static bool IsFixedLocalDrive(string path)
    {
        try
        {
            string? root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            return new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch
        {
            return false;
        }
    }
}
