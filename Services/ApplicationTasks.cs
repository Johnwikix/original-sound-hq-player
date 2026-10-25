using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>应用操作的停止屏障；取消只发送请求，DrainAsync 等待实际任务结束。</summary>
public sealed class ApplicationTasks(AppLifecycle lifecycle)
{
    private readonly Lock _gate = new();
    private readonly HashSet<Task> _tasks = [];
    private readonly HashSet<Task> _committed = [];
    private bool _closed, _committedClosed;

    /// <summary>Accepted local edits start immediately and drain before their database owner stops.</summary>
    public Task RunCommittedAsync(Func<Task> operation)
    {
        lock (_gate)
        {
            if (_closed || _committedClosed || lifecycle.StoppingToken.IsCancellationRequested) return Task.CompletedTask;
            var task = operation();
            _tasks.Add(task);
            _committed.Add(task);
            return ObserveCompletionAsync(task);
        }
    }

    public Task RunAsync(Func<CancellationToken, Task> operation)
    {
        lock (_gate)
        {
            if (_closed || lifecycle.StoppingToken.IsCancellationRequested) return Task.CompletedTask;
            var task = RunDeferredAsync(operation);
            _tasks.Add(task);
            return ObserveCompletionAsync(task);
        }
    }

    private async Task RunDeferredAsync(Func<CancellationToken, Task> operation)
    {
        await Task.Yield();
        if (!lifecycle.StoppingToken.IsCancellationRequested) await operation(lifecycle.StoppingToken);
    }

    private async Task ObserveCompletionAsync(Task task)
    {
        try { await task; }
        finally
        {
            lock (_gate)
            {
                _tasks.Remove(task);
                _committed.Remove(task);
            }
        }
    }

    public Task DrainCommittedAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            _committedClosed = true;
            pending = new Task[_committed.Count];
            _committed.CopyTo(pending);
        }
        return Task.WhenAll(pending);
    }

    public async Task DrainAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            _closed = true;
            pending = new Task[_tasks.Count];
            _tasks.CopyTo(pending);
        }
        await Task.WhenAll(pending);
    }
}
