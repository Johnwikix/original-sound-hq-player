namespace AudioPlayer.Decode;

/// <summary>Engine-owned, bounded metadata cache. Access is serialized by the engine control lock.</summary>
internal sealed class AtmosProbeCache
{
    internal const int Capacity = 128;
    private readonly record struct FileStamp(long Length, long ModifiedUtc);
    private readonly record struct Entry(FileStamp Stamp, Eac3BitstreamReader.StreamInfo? Info);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _insertionOrder = new();

    private static (string Path, FileStamp Stamp)? Identify(string path)
    {
        try
        {
            var file = new FileInfo(path);
            return file.Exists ? (file.FullName, new(file.Length, file.LastWriteTimeUtc.Ticks)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // Unavailable files and non-file URLs are never negatively cached.
        }
    }

    public Eac3BitstreamReader? TryOpen(string path)
    {
        var identity = Identify(path);
        Eac3BitstreamReader.StreamInfo? cachedInfo = null;
        if (identity is { } file && _entries.TryGetValue(file.Path, out var entry) && entry.Stamp == file.Stamp)
        {
            if (entry.Info == null) return null;
            cachedInfo = entry.Info;
        }
        var reader = new Eac3BitstreamReader();
        try
        {
            bool opened = reader.Open(path, cachedInfo);
            // Probe failures are transient. Cache only completed probes of an unchanged local file.
            if (reader.ProbeCompleted && identity is { } before && Identify(path) == identity)
            {
                if (!_entries.ContainsKey(before.Path))
                {
                    if (_entries.Count >= Capacity) _entries.Remove(_insertionOrder.Dequeue());
                    _insertionOrder.Enqueue(before.Path);
                }
                _entries[before.Path] = new(before.Stamp, reader.ProbedInfo);
            }
            if (opened) return reader;
            reader.Dispose();
            return null;
        }
        catch { reader.Dispose(); throw; }
    }
}
