namespace OriginalSound.Plugin.Runtime;

/// <summary>Seeds a complete bundled plugin once, without replacing a user's installed version.</summary>
public static class BundledPluginInstaller
{
    public static void InstallIfMissing(string source, string destination, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Directory.Exists(destination) || !Directory.Exists(source)) return;
        string parent = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(parent);
        // Keep incomplete copies outside the scanned Plugins directory. Rename publishes the whole package.
        string staging = Path.Combine(Path.GetDirectoryName(parent)!, ".plugin-install-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(staging);
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string target = Path.Combine(staging, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(Path.Combine(staging, "plugin.json")))
                throw new InvalidDataException("Bundled plugin manifest is missing.");
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination)) { /* Another startup installed it first. */ }
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }
}
