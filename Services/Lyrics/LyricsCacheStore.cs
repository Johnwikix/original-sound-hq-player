using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services.Lyrics;

public sealed class LyricsCacheEntry
{
    public int SchemaVersion { get; set; } = 2;
    public long Revision { get; set; } = 1;
    public string Path { get; set; } = "";
    public LyricsDocument Document { get; set; } = LyricsDocument.Empty;
    public DateTime CachedAtUtc { get; set; }
    public string Diagnostic { get; set; } = "";
    public string SourceKind { get; set; } = "";
}

internal sealed class LegacyLyricsCacheEntry
{
    public string Path { get; set; } = "";
    public string? Lrc { get; set; }
    public string? Trans { get; set; }
    public string? Krc { get; set; }
    public string? TKrc { get; set; }
    public DateTime CachedAtUtc { get; set; }
}

[JsonSerializable(typeof(LyricsCacheEntry))]
[JsonSerializable(typeof(LegacyLyricsCacheEntry))]
internal partial class LyricsCacheJsonContext : JsonSerializerContext { }

/// <summary>Bounded, versioned cache. Readers and writers share a lock; legacy JSON stays recoverable.</summary>
public sealed class LyricsCacheStore(string directory, LyricsParser parser)
{
    private readonly object _gate = new();
    private string FileName(string path) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))) + ".json");

    public LyricsCacheEntry? Load(string path)
    {
        lock (_gate)
        {
            string file = FileName(path);
            if (!File.Exists(file)) return null;
            if (new FileInfo(file).Length > 4 * LyricsParser.MaxContentLength) return null;
            string text = File.ReadAllText(file);
            using var json = JsonDocument.Parse(text);
            if (json.RootElement.TryGetProperty("SchemaVersion", out var version))
            {
                if (version.GetInt32() != 2) return null; // Preserve unknown future versions.
                var current = JsonSerializer.Deserialize(text, LyricsCacheJsonContext.Default.LyricsCacheEntry);
                return current is not null && current.Document?.Original is not null && string.Equals(current.Path, path, StringComparison.OrdinalIgnoreCase) ? current : null;
            }
            var legacy = JsonSerializer.Deserialize(text, LyricsCacheJsonContext.Default.LegacyLyricsCacheEntry);
            if (legacy is null || !string.Equals(legacy.Path, path, StringComparison.OrdinalIgnoreCase)) return null;
            var migrated = LyricsLegacyMigration.Convert(new MusicLyrics { Lyrics = legacy.Lrc ?? "", TranslatedLyrics = legacy.Trans ?? "",
                Krc = legacy.Krc ?? "", TKrc = legacy.TKrc ?? "" }, parser);
            var entry = new LyricsCacheEntry { Path = path, Document = migrated.Snapshot().Document, CachedAtUtc = legacy.CachedAtUtc, Diagnostic = migrated.Diagnostic };
            Backup(file);
            Write(entry);
            return entry;
        }
    }

    public bool Save(string path, LyricsDocument document, long? expectedRevision = null, string sourceKind = "Online")
    {
        lock (_gate)
        {
            string file = FileName(path);
            long revision = 0;
            if (File.Exists(file))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(file));
                    if (json.RootElement.TryGetProperty("SchemaVersion", out var version) && version.GetInt32() != 2) return false;
                    revision = Load(path)?.Revision ?? 0; // Migrate and preserve legacy candidates before replacement.
                }
                catch (JsonException)
                {
                    Backup(file);
                }
            }
            if (expectedRevision is { } expected && revision != expected) return false;
            Write(new() { Path = path, Document = document, CachedAtUtc = DateTime.UtcNow, Revision = revision + 1, SourceKind = sourceKind });
            return true;
        }
    }

    private void Backup(string file)
    {
        string recovery = Path.Combine(directory, "legacy");
        Directory.CreateDirectory(recovery);
        string target = Path.Combine(recovery, Path.GetFileName(file));
        if (File.Exists(target))
        {
            if (File.ReadAllText(target) == File.ReadAllText(file)) return;
            target = Path.Combine(recovery, Path.GetFileNameWithoutExtension(file) + "." + Guid.NewGuid().ToString("N") + ".json");
        }
        File.Copy(file, target);
        Trim(recovery);
    }

    private void Write(LyricsCacheEntry entry)
    {
        Directory.CreateDirectory(directory);
        string file = FileName(entry.Path);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entry, LyricsCacheJsonContext.Default.LyricsCacheEntry));
            File.Move(temporary, file, true);
            Trim(directory);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Trim(string folder)
    {
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*.json").OrderByDescending(file => file.LastWriteTimeUtc).Skip(300))
            try { file.Delete(); } catch (IOException) { }
    }
}
