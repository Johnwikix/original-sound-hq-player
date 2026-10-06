using System.Buffers.Binary;

namespace BassPlayerIpc.Shared;

/// <summary>Reliable bounded notifications plus one pending snapshot per state type; publishers never wait for I/O.</summary>
public sealed class PipeStateServer : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly TaskCompletionSource _overflowed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<(MessageTypeId Type, byte[] Payload)> _notifications = new();
    private readonly byte[] _dspPayload = new byte[DspProtocol.StateSize];
    // Keep only the unavailable marker while FFT is disabled; the maximum-sized
    // frame buffer is allocated lazily for an active visualizer.
    private byte[] _fftPayload = new byte[FftProtocol.HeaderSize];
    private ProgressSnapshot _progress;
    private DspStateSnapshot? _dsp;
    private int _fftLength;
    private bool _progressPending, _dspPending, _fftPending, _preferFft, _overflow, _disposed;
    public DspStateSnapshot? CurrentDspState { get { lock (_gate) return _dsp; } }

    public void PublishProgress(ProgressSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _progress = snapshot with { Revision = checked(_progress.Revision + 1) };
            _progressPending = true;
            Wake();
        }
    }

    public bool PublishDsp(DspState state)
    {
        lock (_gate)
        {
            if (_disposed || _dsp?.State == state) return false;
            // Validate before committing. An invalid device identity must not replace the last
            // valid snapshot or turn a publisher error into a fatal background writer failure.
            Span<byte> payload = stackalloc byte[DspProtocol.StateSize];
            DspProtocol.WriteState(payload, state);
            payload.CopyTo(_dspPayload);
            _dsp = new(checked((_dsp?.Revision ?? 0) + 1), state);
            _dspPending = true;
            Wake();
            return true;
        }
    }

    /// <summary>Stores only the newest FFT frame; publishing never waits for pipe I/O.</summary>
    public bool PublishFft(FftSnapshot snapshot)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            int capacity = snapshot.IsAvailable ? FftProtocol.MaxPayloadSize : FftProtocol.HeaderSize;
            if (_fftPayload.Length != capacity)
                _fftPayload = new byte[capacity];
            int length = FftProtocol.Write(_fftPayload, snapshot);
            _fftLength = length;
            _fftPending = true;
            Wake();
            return true;
        }
    }

    public void PublishNotification(MessageTypeId type, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > IpcConstants.MaxNotificationSize) throw new ArgumentOutOfRangeException(nameof(payload));
        lock (_gate)
        {
            if (_disposed) return;
            // A critical event cannot be silently replaced. A stalled reader instead fails the connection.
            if (_notifications.Count >= 512)
            {
                _overflow = true;
                _overflowed.TrySetResult();
            }
            else _notifications.Enqueue((type, payload.ToArray()));
            Wake();
        }
    }

    private void Wake()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    public async Task RunAsync(string name, Guid instanceId, CancellationToken token)
    {
        using var pipe = PipeProtocol.CreateServer(name);
        await PipeProtocol.AcceptAsync(pipe, instanceId, token).ConfigureAwait(false);
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        var publishing = PublishAsync(pipe, connection.Token);
        var disconnected = WatchDisconnectAsync(pipe, connection.Token);
        try
        {
            var completed = await Task.WhenAny(publishing, disconnected, _overflowed.Task).ConfigureAwait(false);
            if (completed == _overflowed.Task) throw new IOException("Audio notification queue exceeded its limit.");
            await completed.ConfigureAwait(false);
        }
        finally
        {
            connection.Cancel();
            pipe.Dispose();
            try { await Task.WhenAll(publishing, disconnected).ConfigureAwait(false); }
            catch (Exception) when (connection.IsCancellationRequested) { }
        }
    }

    private static async Task WatchDisconnectAsync(Stream pipe, CancellationToken token)
    {
        await pipe.ReadAsync(new byte[1], token).ConfigureAwait(false);
        throw new IOException("Audio state subscriber disconnected or sent unexpected data.");
    }

    private async Task PublishAsync(Stream pipe, CancellationToken token)
    {
        var writer = new PipeFrameWriter();
        byte[] buffer = new byte[IpcConstants.MaxStatePayloadSize];
        while (true)
        {
            token.ThrowIfCancellationRequested();
            PipeFrameKind kind;
            int type = 0;
            long id = 0;
            ReadOnlyMemory<byte> payload;
            lock (_gate)
            {
                if (_overflow) throw new IOException("Audio notification queue exceeded its limit.");
                if (_notifications.TryDequeue(out var notification))
                {
                    kind = PipeFrameKind.Notification;
                    type = (int)notification.Type;
                    payload = notification.Payload;
                }
                else if (_dspPending)
                {
                    _dspPending = false;
                    kind = PipeFrameKind.DspState;
                    id = _dsp!.Revision;
                    _dspPayload.CopyTo(buffer, 0);
                    payload = buffer.AsMemory(0, DspProtocol.StateSize);
                }
                else if (_progressPending)
                {
                    if (_fftPending && _preferFft)
                    {
                        _fftPending = false;
                        _preferFft = false;
                        kind = PipeFrameKind.FftData;
                        id = BinaryPrimitives.ReadInt64LittleEndian(_fftPayload);
                        _fftPayload.AsSpan(0, _fftLength).CopyTo(buffer);
                        payload = buffer.AsMemory(0, _fftLength);
                    }
                    else
                    {
                        _progressPending = false;
                        _preferFft = true;
                        kind = PipeFrameKind.Progress;
                        id = _progress.Revision;
                        ProgressProtocol.Write(buffer, _progress);
                        payload = buffer.AsMemory(0, ProgressProtocol.Size);
                    }
                }
                else if (_fftPending)
                {
                    _fftPending = false;
                    _preferFft = false;
                    kind = PipeFrameKind.FftData;
                    id = BinaryPrimitives.ReadInt64LittleEndian(_fftPayload);
                    _fftPayload.AsSpan(0, _fftLength).CopyTo(buffer);
                    payload = buffer.AsMemory(0, _fftLength);
                }
                else { kind = 0; payload = default; }
            }
            if (kind == 0)
            {
                await _changed.WaitAsync(token).ConfigureAwait(false);
                continue;
            }
            await writer.WriteAsync(pipe, kind, id, type, payload, token).ConfigureAwait(false);
        }
    }

    // The owner cancels and awaits RunAsync before releasing the wake handle.
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _notifications.Clear();
            _fftPayload = Array.Empty<byte>();
            _changed.Dispose();
        }
    }
}
