using System.Text.Json;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

namespace AudioPlayer;

/// <summary>使用独立的持久控制通道，使准备或定位期间仍可停止和暂停。</summary>
internal sealed class StreamingServer(PlaybackEngine engine, Guid instanceId)
{
    public Task RunAsync(CancellationToken token)
        => new PipeCommandServer(StreamingWire.PipeName, instanceId, Handle, instances: 4).RunAsync(token);

    private PipeResponse Handle(CommandId id, ReadOnlySpan<byte> payload)
    {
        try
        {
            if (id != CommandId.StreamControl || payload.Length > StreamingWire.MaxPayload)
                return new(MessageTypeId.Failed, ReadOnlyMemory<byte>.Empty);
            var command = JsonSerializer.Deserialize(payload, StreamingJson.Default.StreamCommand)
                ?? throw new InvalidDataException("Empty streaming command.");
            var reply = engine.HandleStreamCommand(command);
            return new(MessageTypeId.Success, JsonSerializer.SerializeToUtf8Bytes(reply, StreamingJson.Default.StreamReply));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            return new(MessageTypeId.Failed, ReadOnlyMemory<byte>.Empty);
        }
    }
}
