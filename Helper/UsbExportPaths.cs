using System.IO;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Helper;

internal static class UsbExportPaths
{
    internal static string GetFolderName(string name)
    {
        string sanitized = ToolUtils.SanitizeFileName(name, Path.GetInvalidFileNameChars());
        return string.IsNullOrWhiteSpace(sanitized) ? "_" : sanitized;
    }

    internal static string GetRelativeFilePath(Music music, string extension)
    {
        string fileName = ToolUtils.SanitizeFileName(Path.GetFileName(music.Path), Path.GetInvalidFileNameChars());
        return Path.Combine("MUSIC", GetFolderName(music.Author), GetFolderName(music.Album),
            Path.ChangeExtension(fileName, "." + extension));
    }
}
