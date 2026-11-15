using System;
using System.IO;
using System.Globalization;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper;

internal static class UsbExportPaths
{
    /// <summary>台账身份以设备根目录为基准，不把可变盘符作为文件身份的一部分。</summary>
    internal static string GetStoragePathKey(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        string relative = root.Length > 0 ? path[root.Length..] : path;
        return relative.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    internal static bool IsMusicPathKey(string path)
        => path.StartsWith("MUSIC" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    internal static string GetSourceIdentity(Music music)
        => music.Id > 0 ? "music:" + music.Id.ToString(CultureInfo.InvariantCulture) : "path:" + Path.GetFullPath(music.Path);

    internal static string GetFolderName(string? name) => Normalize(name ?? string.Empty);

    internal static string GetFileName(string name) => Normalize(name);

    private static string Normalize(string name)
    {
        string sanitized = ToolUtils.SanitizeFileName(name, Path.GetInvalidFileNameChars()).TrimEnd(' ', '.');
        if (string.IsNullOrWhiteSpace(sanitized)) return "_";
        int dot = sanitized.IndexOf('.');
        ReadOnlySpan<char> stem = dot < 0 ? sanitized.AsSpan() : sanitized.AsSpan(0, dot);
        bool reserved = stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || stem.Length == 4
                && (stem[..3].Equals("COM", StringComparison.OrdinalIgnoreCase) || stem[..3].Equals("LPT", StringComparison.OrdinalIgnoreCase))
                && (stem[3] is >= '1' and <= '9' or '\u00b9' or '\u00b2' or '\u00b3');
        return reserved ? "_" + sanitized : sanitized;
    }

    internal static string GetRelativeFilePath(Music music, string extension)
    {
        string fileName = GetFileName(Path.GetFileName(music.Path));
        return Path.Combine("MUSIC", GetFolderName(music.Author), GetFolderName(music.Album),
            Path.ChangeExtension(fileName, "." + extension));
    }
}
