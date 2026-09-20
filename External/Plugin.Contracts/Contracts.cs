using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace OriginalSound.Plugin;

public sealed record PluginManifest
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public int ApiVersion { get; init; } = 1;
    public string EntryAssembly { get; init; } = "";
    public string EntryType { get; init; } = "";
    public string[] Capabilities { get; init; } = [];
    public PluginPage[] Pages { get; init; } = [];
}

// V1 deliberately supports a bounded set of native page templates. No plugin XAML executes in the UI process.
public sealed record PluginPage(string Id, string Title, string Kind);
public sealed record TrackMetadata(string Id, string Title, string Artist, string Album, long? DurationMs, string Language);
public enum PluginResult { Found, NoResult, Unavailable, NetworkError, Unsupported }
public sealed record LyricsCandidate(string Id, string Title, string Source, string Format, string Text, string Translation);
public sealed record PluginRequest
{
    public long Id { get; init; }
    public string Method { get; init; } = "";
    public TrackMetadata? Track { get; init; }
    public bool WordSynced { get; init; }
    public string? ResourceId { get; init; }
}
public sealed record PluginReply
{
    public long Id { get; init; }
    public int ApiVersion { get; init; } = 1;
    public PluginResult Result { get; init; }
    public LyricsCandidate[] Lyrics { get; init; } = [];
    public byte[]? Cover { get; init; }
    public string? Error { get; init; }
    public ResolvedSource? Source { get; init; }
}

public sealed record TrackIdentity(string ProviderId, string TrackId);
public sealed record PlaybackEntry(TrackIdentity Identity, TrackMetadata Metadata, string ResourceId, bool IsLocal);
public sealed record ResolvedSource(string Url, Dictionary<string, string> Headers, DateTimeOffset? ExpiresAt,
    bool CanSeek, bool IsLive = false);

public interface IPlayerPlugin : IAsyncDisposable
{
    ValueTask InitializeAsync(string dataDirectory, CancellationToken cancellationToken);
    ValueTask<PluginReply> HandleAsync(PluginRequest request, CancellationToken cancellationToken);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(PluginRequest))]
[JsonSerializable(typeof(PluginReply))]
[JsonSerializable(typeof(string[]))]
public partial class PluginJson : JsonSerializerContext;

public static class PluginWire
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public static async ValueTask WriteAsync<T>(Stream stream, T value, JsonTypeInfo<T> type, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, type);
        if (body.Length > MaxBytes) throw new InvalidDataException("Plugin response exceeds the protocol limit.");
        byte[] size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, body.Length);
        await stream.WriteAsync(size, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
    public static async ValueTask<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken ct)
    {
        byte[] size = new byte[4];
        await stream.ReadExactlyAsync(size, ct);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is <= 0 or > MaxBytes) throw new InvalidDataException("Invalid plugin frame length.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize(body, type) ?? throw new InvalidDataException("Empty plugin frame.");
    }
}
