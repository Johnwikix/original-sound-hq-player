using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>只执行启动时登记的操作；退出时不从容器解析或创建对象。UI 线程调用。</summary>
public sealed class ShutdownCoordinator(AppLifecycle lifecycle, ILogger<ShutdownCoordinator> logger)
{
    private readonly List<(string Name, Func<Task> Action)> _save = [];
    private readonly List<(string Name, Func<Task> Action)> _stop = [];
    private readonly Stack<(string Name, Func<Task> Action)> _cleanup = [];
    private readonly Lock _gate = new();
    private bool _finished;
    private Func<Task>? _stopHost;
    public void RegisterSave(Func<Task> save, [CallerArgumentExpression(nameof(save))] string name = "save") => _save.Add((name, save));
    /// <summary>停止生产者、等待在途工作后才构建最终保存快照。</summary>
    public void RegisterStop(Func<Task> stop, [CallerArgumentExpression(nameof(stop))] string name = "stop") => _stop.Add((name, stop));
    public void RegisterCleanup(Action cleanup, [CallerArgumentExpression(nameof(cleanup))] string name = "cleanup") => RegisterCleanup(() => { cleanup(); return Task.CompletedTask; }, name);
    public void RegisterCleanup(Func<Task> cleanup, [CallerArgumentExpression(nameof(cleanup))] string name = "cleanup")
    {
        lock (_gate)
        {
            if (!_finished) { _cleanup.Push((name, cleanup)); return; }
        }
        // 例如后台封面任务迟到创建的 HTTP 客户端，不留在已经排空的清理队列中。
        _ = RunStepAsync(name, cleanup);
    }
    public void HostStarted(Func<Task> stopHost) => _stopHost = stopHost;

    public async Task<bool> ShutdownAsync()
    {
        bool wasReady = false;
        try
        {
            if (!lifecycle.TryBeginExit(out wasReady)) return false;
        }
        catch (Exception ex) when (lifecycle.Phase == AppPhase.Stopping)
        {
            // 阶段已经改变；取消/状态订阅者异常不能使后续请求全部被挡住却无人清理。
            logger.LogError(ex, "退出通知失败，继续保存和清理资源");
        }
        for (int i = 0; i < _stop.Count; i++)
            await RunStepAsync(_stop[i].Name, _stop[i].Action);
        if (wasReady)
            for (int i = 0; i < _save.Count; i++)
                await RunStepAsync(_save[i].Name, _save[i].Action);
        if (_stopHost is not null)
            try { await _stopHost(); }
            catch (Exception ex) { logger.LogError(ex, "停止 Host 失败"); }
        while (true)
        {
            (string Name, Func<Task> Action) cleanup;
            lock (_gate)
            {
                if (!_cleanup.TryPop(out cleanup)) { _finished = true; break; }
            }
            await RunStepAsync(cleanup.Name, cleanup.Action);
        }
        logger.LogInformation("应用程序退出完成");
        return true;
    }
    private async Task RunStepAsync(string name, Func<Task> action)
    {
        long started = Stopwatch.GetTimestamp();
        try { await action(); }
        catch (Exception ex) { logger.LogError(ex, "退出步骤失败: {Step}", name); }
        finally { logger.LogInformation("退出步骤完成: {Step}, {ElapsedMs} ms", name, Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }
}
