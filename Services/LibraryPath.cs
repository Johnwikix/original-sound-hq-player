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
}
