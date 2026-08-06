using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BassPlayerIpc.Shared;

public static class IpcEnvelope
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteCommand(
        MemoryMappedViewAccessor accessor,
        long offset,
        CommandId commandId,
        byte sequenceId,
        scoped ReadOnlySpan<byte> payload,
        int maxSize = IpcConstants.MaxRequestSize)
    {
        WriteEnvelope(accessor, offset, (short)commandId, sequenceId, payload, maxSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void WriteResponse(
        MemoryMappedViewAccessor accessor,
        long offset,
        MessageTypeId typeId,
        byte sequenceId,
        scoped ReadOnlySpan<byte> payload,
        int maxSize = IpcConstants.MaxResponseSize)
    {
        WriteEnvelope(accessor, offset, (short)typeId, sequenceId, payload, maxSize);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteEnvelope(
        MemoryMappedViewAccessor accessor,
        long offset,
        short typeId,
        byte sequenceId,
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
        accessor.Write(offset + 4, sequenceId);

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
    public static byte ReadSequenceId(MemoryMappedViewAccessor accessor, long offset)
    {
        return accessor.ReadByte(offset + 4);
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

    /// <summary>
    /// Reads a mailbox version int. The trailing memory barrier ensures the
    /// payload reads that follow see all writes published before the version.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ReadVersion(MemoryMappedViewAccessor accessor, long offset)
    {
        int version = accessor.ReadInt32(offset);
        Thread.MemoryBarrier();
        return version;
    }

    /// <summary>
    /// Publishes a mailbox version int. The barriers ensure payload writes
    /// complete before the version becomes visible to the peer process.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void PublishVersion(MemoryMappedViewAccessor accessor, long offset, int value)
    {
        Thread.MemoryBarrier();
        accessor.Write(offset, value);
        Thread.MemoryBarrier();
    }

    /// <summary>
    /// Notification buffers are double-buffered: the slot is selected by the
    /// published version parity, so the peer can never read a slot that is
    /// concurrently being overwritten.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long NotificationSlotOffset(int version)
    {
        return (version & 1) == 0
            ? IpcConstants.NotificationSlot1Offset
            : IpcConstants.NotificationSlot2Offset;
    }
}
