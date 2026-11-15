using System;
using System.IO;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper;

internal static class UsbExportPaths
{
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
