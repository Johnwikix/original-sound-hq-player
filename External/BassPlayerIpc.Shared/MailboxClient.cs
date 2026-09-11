using System.Buffers;
using System.Collections.Generic;
using System.IO.MemoryMappedFiles;

namespace BassPlayerIpc.Shared;

/// <summary>串行交付共享邮箱命令；执行确认前不复用请求槽，高频状态仅在有序屏障内合并。</summary>
public sealed class MailboxClient : IDisposable
{
    private sealed class Pending
    {
        internal CommandId Command;
        internal byte[] Payload = [];
        internal int Length;
        internal byte[] Response = [];
        internal int Timeout;
        internal bool Coalesce;
        internal TaskCompletionSource<(MessageTypeId Type, int Length)>? Completion;
    }

    private readonly MemoryMappedViewAccessor _view;
    private readonly Semaphore _request, _response;
    private readonly AutoResetEvent _queued = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly object _gate = new();
    private readonly LinkedList<Pending> _pending = new();
    private readonly Thread _worker;
    private bool _disposed, _busy;
    private int _version, _disposeStarted;
    private const int MaxQueued = 512;

    /// <summary>报告未成功应用的命令；在后台发送线程调用。</summary>
    public event Action<CommandId>? CommandFailed;

    /// <summary>报告终止发送线程的传输异常。</summary>
    public event Action<Exception>? Faulted;

    /// <summary>初始化邮箱发送线程；共享内存和信号量的生命周期仍归调用者所有。</summary>
    public MailboxClient(MemoryMappedViewAccessor view, Semaphore request, Semaphore response)
    {
        _view = view; _request = request; _response = response;
        _version = IpcEnvelope.ReadVersion(view, IpcConstants.RequestVersionOffset);
        _worker = new Thread(Run) { IsBackground = true, Name = "audio-ipc-send" };
        _worker.Start();
    }

    /// <summary>发布无需调用者等待的命令；返回 false 表示连接关闭或队列已满。</summary>
    public bool Publish(CommandId command, ReadOnlySpan<byte> payload, bool coalesce = false)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            if (coalesce)
            {
                for (var node = _pending.Last; node != null && node.Value.Coalesce; node = node.Previous)
                {
                    var old = node.Value;
                    if (old.Command != command) continue;
                    // .NET 11：重复滑动复用待发缓冲，不为每个值分配任务或队列节点。
                    CopyPayload(old, payload);
                    return true;
                }
            }
            if (_pending.Count >= MaxQueued) return false;
            var work = new Pending { Command = command, Coalesce = coalesce, Timeout = 1000 };
            CopyPayload(work, payload);
            _pending.AddLast(work);
            _queued.Set();
            return true;
        }
    }

    /// <summary>发送有序请求并获取执行结果；忙时可跳过进度轮询。</summary>
    public Task<(MessageTypeId Type, int Length)> RequestAsync(CommandId command, ReadOnlySpan<byte> payload,
        byte[] response, int timeoutMs = 1000, bool skipIfBusy = false)
    {
        lock (_gate)
        {
            if (_disposed || _pending.Count >= MaxQueued || (skipIfBusy && (_busy || _pending.Count != 0)))
                return Task.FromResult((MessageTypeId.Failed, 0));
            var work = new Pending
            {
                Command = command, Response = response, Timeout = Math.Max(1, timeoutMs),
                Completion = new(TaskCreationOptions.RunContinuationsAsynchronously),
            };
            CopyPayload(work, payload);
            _pending.AddLast(work);
            _queued.Set();
            return work.Completion.Task;
        }
    }

    private static void CopyPayload(Pending work, ReadOnlySpan<byte> payload)
    {
        if (payload.Length > IpcConstants.MaxRequestSize - IpcConstants.EnvelopeHeaderSize)
            throw new ArgumentOutOfRangeException(nameof(payload));
        if (work.Payload.Length < payload.Length)
        {
            if (work.Payload.Length != 0) ArrayPool<byte>.Shared.Return(work.Payload);
            work.Payload = ArrayPool<byte>.Shared.Rent(payload.Length);
        }
        payload.CopyTo(work.Payload);
        work.Length = payload.Length;
    }

    private void Run()
    {
        WaitHandle[] queueWait = [_stop, _queued];
        WaitHandle[] responseWait = [_stop, _response];
        byte[] buffer = new byte[IpcConstants.MaxResponseSize];
        try
        {
            while (!_stop.WaitOne(0))
            {
                Pending? work;
                lock (_gate)
                {
                    work = _pending.First?.Value;
                    if (work != null) _pending.RemoveFirst();
                    _busy = work != null;
                }
                if (work == null)
                {
                    if (WaitHandle.WaitAny(queueWait) == 0) break;
                    continue;
                }
                try
                {
                    int version = unchecked(++_version);
                    IpcEnvelope.WriteCommand(_view, IpcConstants.RequestBufferOffset, work.Command, (byte)version,
                        work.Payload.AsSpan(0, work.Length));
                    IpcEnvelope.PublishVersion(_view, IpcConstants.RequestVersionOffset, version);
                    try { _request.Release(); } catch (SemaphoreFullException) { }
                    long started = Environment.TickCount64;
                    bool timedOut = false;
                    while (IpcEnvelope.ReadVersion(_view, IpcConstants.ResponseVersionOffset) != version)
                    {
                        int remaining = work.Timeout - (int)Math.Min(int.MaxValue, Environment.TickCount64 - started);
                        if (!timedOut && remaining <= 0)
                        {
                            timedOut = true;
                            work.Completion?.TrySetResult((MessageTypeId.Failed, 0));
                            // 超时只结束调用者等待，仍须等消费确认；绝不覆盖服务端在途请求。
                        }
                        if (WaitHandle.WaitAny(responseWait, timedOut ? 1000 : Math.Max(1, remaining)) == 0)
                            return;
                    }
                    var type = IpcEnvelope.ReadMessageTypeId(_view, IpcConstants.ResponseBufferOffset);
                    int length = IpcEnvelope.ReadPayload(_view, IpcConstants.ResponseBufferOffset, buffer,
                        IpcConstants.MaxResponseSize - IpcConstants.EnvelopeHeaderSize);
                    if (length < 0 || (work.Completion != null && length > work.Response.Length && type != MessageTypeId.Failed))
                    { type = MessageTypeId.Failed; length = 0; }
                    // 超时后调用者可能归还自己的池缓冲，不能再写入该缓冲。
                    if (!timedOut && length > 0 && length <= work.Response.Length)
                        buffer.AsSpan(0, length).CopyTo(work.Response);
                    work.Completion?.TrySetResult((type, length));
                    if (type == MessageTypeId.Failed) CommandFailed?.Invoke(work.Command);
                }
                finally
                {
                    work.Completion?.TrySetResult((MessageTypeId.Failed, 0));
                    if (work.Payload.Length != 0) ArrayPool<byte>.Shared.Return(work.Payload);
                }
            }
        }
        catch (Exception ex)
        {
            try { Faulted?.Invoke(ex); } catch { }
        }
        finally
        {
            lock (_gate)
            {
                _disposed = true;
                foreach (var work in _pending)
                {
                    work.Completion?.TrySetResult((MessageTypeId.Failed, 0));
                    if (work.Payload.Length != 0) ArrayPool<byte>.Shared.Return(work.Payload);
                }
                _pending.Clear();
                _busy = false;
            }
        }
    }

    /// <summary>停止发送并等待后台线程退出，之后调用者可以释放共享内存。</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) != 0) return;
        lock (_gate) { _disposed = true; _stop.Set(); }
        _worker.Join();
        _stop.Dispose();
        _queued.Dispose();
    }
}
