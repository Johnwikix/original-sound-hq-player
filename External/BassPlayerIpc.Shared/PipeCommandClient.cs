using System.Buffers;
using System.IO.Pipes;

namespace BassPlayerIpc.Shared;

/// <summary>Persistent ordered commands; timeout/cancellation ends the caller's wait, not server execution.</summary>
public sealed class PipeCommandClient : IDisposable, IAsyncDisposable
{
    private sealed class Pending
    {
        public CommandId Command;
        public byte[] Payload = [];
        public int Length;
        public byte[] Response = [];
        public int Timeout;
        public bool Coalesce;
        public TaskCompletionSource<(MessageTypeId Type, int Length)>? Completion;
        public CancellationTokenRegistration Cancellation;

        // Completion and copying share a lock: callers may return their response buffer as soon as
        // cancellation/timeout completes the task. A late reply must never touch that buffer.
        public void Complete(MessageTypeId type, ReadOnlySpan<byte> payload)
        {
            lock (this)
            {
                if (Completion is null || Completion.Task.IsCompleted) return;
                if (payload.Length > Response.Length)
                {
                    Completion.TrySetResult((MessageTypeId.Failed, 0));
                    return;
                }
                payload.CopyTo(Response);
                Completion.TrySetResult((type, payload.Length));
            }
        }

        public void Cancel(CancellationToken token)
        {
            lock (this) Completion?.TrySetCanceled(token);
        }

        public void Release()
        {
            Cancellation.Dispose();
            Complete(MessageTypeId.Failed, []);
            if (Payload.Length != 0) ArrayPool<byte>.Shared.Return(Payload);
        }
    }

    private const int MaxQueued = 512;
    private readonly NamedPipeClientStream _pipe;
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _queued = new(0, 1);
    private readonly object _gate = new();
    private readonly LinkedList<Pending> _pending = new();
    private readonly Task _worker;
    private bool _stopped, _busy;
    private int _resourcesDisposed;
    public Guid InstanceId { get; }
    public bool IsConnected { get { lock (_gate) return !_stopped; } }
    public event Action<CommandId>? CommandFailed;
    public event Action<Exception>? Faulted;

    private PipeCommandClient(NamedPipeClientStream pipe, Guid instanceId)
    {
        _pipe = pipe;
        InstanceId = instanceId;
        _worker = Task.Run(RunAsync);
    }

    public static async Task<PipeCommandClient> ConnectAsync(string name, CancellationToken token = default)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            var instanceId = await PipeProtocol.ConnectAsync(pipe, token).ConfigureAwait(false);
            return new(pipe, instanceId);
        }
        catch { pipe.Dispose(); throw; }
    }

    public bool Publish(CommandId command, ReadOnlySpan<byte> payload, bool coalesce = false)
    {
        lock (_gate)
        {
            if (_stopped) return false;
            if (coalesce)
            {
                for (var node = _pending.Last; node is not null && node.Value.Coalesce; node = node.Previous)
                {
                    if (node.Value.Command != command) continue;
                    CopyPayload(node.Value, payload);
                    return true;
                }
            }
            if (_pending.Count >= MaxQueued) return false;
            var work = new Pending { Command = command, Coalesce = coalesce, Timeout = 1000 };
            CopyPayload(work, payload);
            _pending.AddLast(work);
            Wake();
            return true;
        }
    }

    public Task<(MessageTypeId Type, int Length)> RequestAsync(CommandId command, ReadOnlySpan<byte> payload,
        byte[] response, int timeoutMs = 1000, bool skipIfBusy = false, CancellationToken token = default)
    {
        if (token.IsCancellationRequested) return Task.FromCanceled<(MessageTypeId, int)>(token);
        lock (_gate)
        {
            if (_stopped || _pending.Count >= MaxQueued || (skipIfBusy && (_busy || _pending.Count != 0)))
                return Task.FromResult((MessageTypeId.Failed, 0));
            var work = new Pending
            {
                Command = command, Response = response, Timeout = Math.Max(1, timeoutMs),
                Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            CopyPayload(work, payload);
            if (token.CanBeCanceled)
                work.Cancellation = token.Register(() => work.Cancel(token));
            _pending.AddLast(work);
            Wake();
            return work.Completion.Task;
        }
    }

    private void Wake()
    {
        if (_queued.CurrentCount == 0) _queued.Release();
    }

    private static void CopyPayload(Pending work, ReadOnlySpan<byte> payload)
    {
        int maximum = work.Command == CommandId.UpdateDeviceCorrections || work.Command == CommandId.StreamControl || work.Command == CommandId.UpdateSettings
            ? IpcConstants.MaxPayloadSize : IpcConstants.MaxRequestSize;
        if (payload.Length > maximum) throw new ArgumentOutOfRangeException(nameof(payload));
        if (work.Payload.Length < payload.Length)
        {
            var next = ArrayPool<byte>.Shared.Rent(payload.Length);
            if (work.Payload.Length != 0) ArrayPool<byte>.Shared.Return(work.Payload);
            work.Payload = next;
        }
        payload.CopyTo(work.Payload);
        work.Length = payload.Length;
    }

    private async Task RunAsync()
    {
        using var reader = new PipeFrameReader();
        var writer = new PipeFrameWriter();
        long requestId = 0;
        Exception? failure = null;
        try
        {
            while (true)
            {
                _stop.Token.ThrowIfCancellationRequested();
                Pending? work;
                lock (_gate)
                {
                    work = _pending.First?.Value;
                    if (work is not null) _pending.RemoveFirst();
                    _busy = work is not null;
                }
                if (work is null)
                {
                    await _queued.WaitAsync(_stop.Token).ConfigureAwait(false);
                    continue;
                }
                try
                {
                    if (work.Completion?.Task.IsCompleted == true) continue;
                    var exchange = ExchangeAsync(work, ++requestId, reader, writer);
                    PipeFrame reply;
                    try { reply = await exchange.WaitAsync(TimeSpan.FromMilliseconds(work.Timeout), _stop.Token).ConfigureAwait(false); }
                    catch (TimeoutException)
                    {
                        work.Complete(MessageTypeId.Failed, []);
                        // Do not resend or deliver the next command until the execution acknowledgement.
                        reply = await exchange.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Observe the underlying I/O before releasing its payload/reader buffers.
                        try { await exchange.ConfigureAwait(false); } catch { }
                        throw;
                    }
                    var type = (MessageTypeId)reply.Type;
                    work.Complete(type, reply.Payload.Span);
                    if (type == MessageTypeId.Failed) CommandFailed?.Invoke(work.Command);
                }
                finally { work.Release(); }
            }
        }
        catch (Exception ex)
        {
            if (!_stop.IsCancellationRequested) failure = ex;
        }
        finally
        {
            Pending[] pending;
            lock (_gate)
            {
                _stopped = true;
                pending = _pending.ToArray();
                _pending.Clear();
                _busy = false;
            }
            foreach (var work in pending) work.Release();
            _pipe.Dispose();
            if (failure is not null)
            {
                try { Faulted?.Invoke(failure); } catch { }
            }
        }
    }

    private async Task<PipeFrame> ExchangeAsync(Pending work, long id, PipeFrameReader reader, PipeFrameWriter writer)
    {
        await writer.WriteAsync(_pipe, PipeFrameKind.Request, id, (int)work.Command,
            work.Payload.AsMemory(0, work.Length), _stop.Token).ConfigureAwait(false);
        var reply = await reader.ReadAsync(_pipe, _stop.Token).ConfigureAwait(false);
        if (reply.Kind != PipeFrameKind.Response || reply.Id != id || reply.Type is < short.MinValue or > short.MaxValue)
            throw new InvalidDataException("Audio command acknowledgement mismatch.");
        return reply;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (!_stopped)
            {
                _stopped = true;
                _stop.Cancel();
                _pipe.Dispose();
            }
        }
        await _worker.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 0)
        {
            _stop.Dispose();
            _queued.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
