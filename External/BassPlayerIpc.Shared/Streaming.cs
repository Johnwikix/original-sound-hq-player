using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace BassPlayerIpc.Shared;

public enum PlaybackSourceKind { LocalFile, Http }
public enum StreamPhase { Opening, Buffering, Ready, Playing, Paused, Ended, Failed, Stopped }
public sealed record BufferPolicy
{
    public int InitialMs { get; init; } = 1500;
    public int ResumeMs { get; init; } = 2500;
    public int CapacityMs { get; init; } = 8000;
    public int OpenTimeoutMs { get; init; } = 20000;
    public int ReadTimeoutMs { get; init; } = 15000;
    public int RetryCount { get; init; } = 2;
    public void Validate()
    {
        if (InitialMs is < 100 or > 10000 || ResumeMs is < 100 or > 10000 ||
            CapacityMs < Math.Max(InitialMs, ResumeMs) || CapacityMs > 30000 ||
            OpenTimeoutMs is < 100 or > 60000 || ReadTimeoutMs is < 100 or > 60000 || RetryCount is < 0 or > 5)
            throw new ArgumentException("Invalid buffering policy.");
    }
}
public sealed record PlaybackSource
{
    public PlaybackSourceKind Kind { get; init; }
    public string ResourceId { get; init; } = "";
    public string Location { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = [];
    public DateTimeOffset? ExpiresAt { get; init; }
    public bool CanSeek { get; init; } = true;
    public bool IsLive { get; init; }
    public BufferPolicy Buffer { get; init; } = new();
    public void Validate()
    {
        Buffer.Validate();
        if (Location.Length is 0 or > 32768 || ResourceId.Length > 1024 || Headers.Count > 32) throw new ArgumentException("Invalid source.");
        if (ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Source expired.");
        if (Kind == PlaybackSourceKind.Http)
        {
            if (!Uri.TryCreate(Location, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
                throw new ArgumentException("Only HTTP(S) sources are supported.");
        }
        else if (Kind != PlaybackSourceKind.LocalFile || !Path.IsPathFullyQualified(Location)) throw new ArgumentException("Invalid local source.");
        foreach (var pair in Headers)
            if (pair.Key.Length is 0 or > 128 || pair.Value.Length > 16384 ||
                pair.Key.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') || pair.Value.Any(c => c is '\r' or '\n' or '\0'))
                throw new ArgumentException("Invalid HTTP header.");
    }
}

public sealed record StreamCommand
{
    public int Version { get; init; } = 1;
    public long RequestId { get; init; }
    public string Method { get; init; } = "capabilities";
    public Guid SessionId { get; init; }
    public PlaybackSource? Source { get; init; }
    public long PositionMs { get; init; }
    public long SeekId { get; init; }
}
public sealed record StreamReply
{
    public int Version { get; init; } = 1;
    public long RequestId { get; init; }
    public Guid SessionId { get; init; }
    public bool Accepted { get; init; }
    public string? Error { get; init; }
    public string[] Capabilities { get; init; } = [];
    public StreamPhase Phase { get; init; }
    public bool WantsPlay { get; init; }
    public bool CanSeek { get; init; }
    public long PositionMs { get; init; }
    public long? DurationMs { get; init; }
    public long BufferedMs { get; init; }
    public long SeekId { get; init; }
}

[JsonSerializable(typeof(StreamCommand))]
[JsonSerializable(typeof(StreamReply))]
public partial class StreamingJson : JsonSerializerContext;

/// <summary>Bounded control channel, separate from real-time mailboxes. Never transports audio samples.</summary>
public static class StreamingWire
{
    public static readonly string PipeName = "OriginalSound_AudioPlayer_Streaming_v1" + IpcConstants.Scope;
    public const int MaxPayload = 128 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken ct)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(message, type);
        if (body.Length > MaxPayload) throw new InvalidDataException("Streaming descriptor too large.");
        byte[] length = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, body.Length);
        await stream.WriteAsync(length, ct);
        await stream.WriteAsync(body, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken ct)
    {
        byte[] length = new byte[4];
        await stream.ReadExactlyAsync(length, ct);
        int count = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is <= 0 or > MaxPayload) throw new InvalidDataException("Invalid streaming frame.");
        byte[] body = new byte[count];
        await stream.ReadExactlyAsync(body, ct);
        return JsonSerializer.Deserialize(body, type) ?? throw new InvalidDataException("Empty streaming frame.");
    }
}

public sealed class StreamingClient
{
    private long _request;
    public async Task<StreamReply> SendAsync(StreamCommand command, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var pipe = new NamedPipeClientStream(".", StreamingWire.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        command = command with { RequestId = Interlocked.Increment(ref _request) };
        await StreamingWire.WriteAsync(pipe, command, StreamingJson.Default.StreamCommand, timeout.Token);
        var response = await StreamingWire.ReadAsync(pipe, StreamingJson.Default.StreamReply, timeout.Token);
        if (response.Version != 1 || response.RequestId != command.RequestId) throw new InvalidDataException("Streaming protocol mismatch.");
        return response;
    }
    public Task<StreamReply> PrepareAsync(PlaybackSource source, Guid sessionId, CancellationToken ct = default)
        => SendAsync(new() { Method = "prepare", Source = source, SessionId = sessionId }, ct);
    public Task<StreamReply> PlayAsync(Guid id, CancellationToken ct = default) => SendAsync(new() { Method = "play", SessionId = id }, ct);
    public Task<StreamReply> PauseAsync(Guid id, CancellationToken ct = default) => SendAsync(new() { Method = "pause", SessionId = id }, ct);
    public Task<StreamReply> StopAsync(Guid id, CancellationToken ct = default) => SendAsync(new() { Method = "stop", SessionId = id }, ct);
    public Task<StreamReply> StatusAsync(Guid id, CancellationToken ct = default) => SendAsync(new() { Method = "status", SessionId = id }, ct);
    public Task<StreamReply> SeekAsync(Guid id, long positionMs, long seekId, CancellationToken ct = default)
        => SendAsync(new() { Method = "seek", SessionId = id, PositionMs = positionMs, SeekId = seekId }, ct);
    public Task<StreamReply> RefreshSourceAsync(Guid id, PlaybackSource source, CancellationToken ct = default)
        => SendAsync(new() { Method = "refresh", SessionId = id, Source = source }, ct);
}
