using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;

namespace BassPlayerIpc.Shared;

public static class IpcEnvelope
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCommand(
        MemoryMappedViewAccessor accessor,
        long offset,
        CommandId commandId,
        scoped ReadOnlySpan<byte> payload,
        int maxSize = IpcConstants.MaxRequestSize)
    {
        WriteEnvelope(accessor, offset, (short)commandId, payload, maxSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteResponse(
        MemoryMappedViewAccessor accessor,
        long offset,
        MessageTypeId typeId,
        scoped ReadOnlySpan<byte> payload,
        int maxSize = IpcConstants.MaxResponseSize)
    {
        WriteEnvelope(accessor, offset, (short)typeId, payload, maxSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEnvelope(
        MemoryMappedViewAccessor accessor,
        long offset,
        short typeId,
        scoped ReadOnlySpan<byte> payload,
        int maxSize)
    {
        int payloadLen = payload.Length;
        int totalLen = IpcConstants.EnvelopeHeaderSize + payloadLen;

        if (totalLen > maxSize)
        {
            payloadLen = maxSize - IpcConstants.EnvelopeHeaderSize;
            if (payloadLen < 0) payloadLen = 0;
        }

        accessor.Write(offset, typeId);
        accessor.Write(offset + 2, (short)payloadLen);

        if (payloadLen > 0)
        {
            byte[] rented = ArrayPool<byte>.Shared.Rent(payloadLen);
            try
            {
                payload[..payloadLen].CopyTo(rented);
                accessor.WriteArray(offset + IpcConstants.EnvelopeHeaderSize, rented, 0, payloadLen);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rented);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static CommandId ReadCommandId(MemoryMappedViewAccessor accessor, long offset)
    {
        return (CommandId)accessor.ReadInt16(offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MessageTypeId ReadMessageTypeId(MemoryMappedViewAccessor accessor, long offset)
    {
        return (MessageTypeId)accessor.ReadInt16(offset);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short ReadPayloadLength(MemoryMappedViewAccessor accessor, long offset)
    {
        return accessor.ReadInt16(offset + 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadPayload(
        MemoryMappedViewAccessor accessor,
        long offset,
        byte[] buffer,
        int maxPayloadSize)
    {
        short payloadLen = accessor.ReadInt16(offset + 2);
        if (payloadLen < 0 || payloadLen > maxPayloadSize || payloadLen > buffer.Length)
            return 0;

        accessor.ReadArray(offset + IpcConstants.EnvelopeHeaderSize, buffer, 0, payloadLen);
        return payloadLen;
    }
}
