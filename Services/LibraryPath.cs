using System;
using System.IO;

namespace WinUIMusicPlayer.Services;

internal static class LibraryPath
{
    public static bool IsWithin(string path, string folder)
    {
        var root = Path.TrimEndingDirectorySeparator(folder.AsSpan());
        return path.AsSpan().Equals(root, StringComparison.OrdinalIgnoreCase)
            || (path.AsSpan().StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && path.Length > root.Length
                && (Path.EndsInDirectorySeparator(root) || (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar)));
    }
}
