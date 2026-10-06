using System.Buffers;
using System.Buffers.Binary;
using System.IO.Pipes;

namespace BassPlayerIpc.Shared;

public enum PipeFrameKind : ushort { Hello = 1, HelloAck, Request, Response, Notification, Progress, DspState, FftData }
public readonly record struct PipeFrame(PipeFrameKind Kind, long Id, int Type, ReadOnlyMemory<byte> Payload);
public readonly record struct PipeResponse(MessageTypeId Type, ReadOnlyMemory<byte> Payload);
public delegate PipeResponse PipeCommandHandler(CommandId command, ReadOnlySpan<byte> payload);

/// <summary>Versioned, bounded byte-stream framing. Each connection has exactly one reader and one writer.</summary>
public static class PipeProtocol
{
    public const uint Magic = 0x5041534F;
    public const ushort Version = 3;
    public const int HeaderSize = 24;

    public static NamedPipeServerStream CreateServer(string name, int instances = 1)
        => new(name, PipeDirection.InOut, instances, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);

    public static async Task<Guid> ConnectAsync(NamedPipeClientStream pipe, CancellationToken token)
    {
        await pipe.ConnectAsync(token).ConfigureAwait(false);
        var writer = new PipeFrameWriter();
        using var reader = new PipeFrameReader(16);
        await writer.WriteAsync(pipe, PipeFrameKind.Hello, 0, 0, ReadOnlyMemory<byte>.Empty, token).ConfigureAwait(false);
        var reply = await reader.ReadAsync(pipe, token).ConfigureAwait(false);
        if (reply.Kind != PipeFrameKind.HelloAck || reply.Id != 0 || reply.Type != 0 || reply.Payload.Length != 16)
            throw new InvalidDataException("Invalid audio pipe handshake.");
        return new Guid(reply.Payload.Span);
    }

    public static async Task AcceptAsync(NamedPipeServerStream pipe, Guid instanceId, CancellationToken token)
    {
        await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var reader = new PipeFrameReader(0);
        var hello = await reader.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
        if (hello.Kind != PipeFrameKind.Hello || hello.Id != 0 || hello.Type != 0 || !hello.Payload.IsEmpty)
            throw new InvalidDataException("Invalid audio pipe handshake.");
        await new PipeFrameWriter().WriteAsync(pipe, PipeFrameKind.HelloAck, 0, 0, instanceId.ToByteArray(), timeout.Token).ConfigureAwait(false);
    }
}

/// <summary>The payload is borrowed until the next read; disposal follows completion of all reads.</summary>
public sealed class PipeFrameReader(int maxPayload = IpcConstants.MaxPayloadSize) : IDisposable
{
    private readonly byte[] _header = new byte[PipeProtocol.HeaderSize];
    private byte[] _payload = [];

    public async ValueTask<PipeFrame> ReadAsync(Stream stream, CancellationToken token)
    {
        await stream.ReadExactlyAsync(_header, token).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32LittleEndian(_header) != PipeProtocol.Magic ||
            BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(4)) != PipeProtocol.Version)
            throw new InvalidDataException("Audio pipe protocol version mismatch.");
        var kind = (PipeFrameKind)BinaryPrimitives.ReadUInt16LittleEndian(_header.AsSpan(6));
        long id = BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(8));
        int type = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(16));
        int length = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(20));
        if (kind is < PipeFrameKind.Hello or > PipeFrameKind.FftData || length < 0 || length > maxPayload)
            throw new InvalidDataException("Invalid audio pipe frame.");
        if (_payload.Length < length)
        {
            var next = ArrayPool<byte>.Shared.Rent(length);
            if (_payload.Length != 0) ArrayPool<byte>.Shared.Return(_payload);
            _payload = next;
        }
        var payload = _payload.AsMemory(0, length);
        await stream.ReadExactlyAsync(payload, token).ConfigureAwait(false);
        return new(kind, id, type, payload);
    }

    public void Dispose()
    {
        if (_payload.Length != 0) ArrayPool<byte>.Shared.Return(_payload);
        _payload = [];
    }
}

public sealed class PipeFrameWriter
{
    private readonly byte[] _header = new byte[PipeProtocol.HeaderSize];
    public async ValueTask WriteAsync(Stream stream, PipeFrameKind kind, long id, int type,
        ReadOnlyMemory<byte> payload, CancellationToken token)
    {
        if (payload.Length > IpcConstants.MaxPayloadSize) throw new ArgumentOutOfRangeException(nameof(payload));
        BinaryPrimitives.WriteUInt32LittleEndian(_header, PipeProtocol.Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(_header.AsSpan(4), PipeProtocol.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(_header.AsSpan(6), (ushort)kind);
        BinaryPrimitives.WriteInt64LittleEndian(_header.AsSpan(8), id);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(16), type);
        BinaryPrimitives.WriteInt32LittleEndian(_header.AsSpan(20), payload.Length);
        await stream.WriteAsync(_header, token).ConfigureAwait(false);
        if (!payload.IsEmpty) await stream.WriteAsync(payload, token).ConfigureAwait(false);
    }
}
