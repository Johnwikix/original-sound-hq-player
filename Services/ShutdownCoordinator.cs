using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>只执行启动时登记的操作；退出时不从容器解析或创建对象。UI 线程调用。</summary>
public sealed class ShutdownCoordinator(AppLifecycle lifecycle, ILogger<ShutdownCoordinator> logger)
{
    private readonly List<Func<Task>> _save = [];
    private readonly Stack<Func<Task>> _cleanup = [];
    private readonly Lock _gate = new();
    private bool _finished;
    private Func<Task>? _stopHost;
    public void RegisterSave(Func<Task> save) => _save.Add(save);
    public void RegisterCleanup(Action cleanup) => RegisterCleanup(() => { cleanup(); return Task.CompletedTask; });
    public void RegisterCleanup(Func<Task> cleanup)
    {
        lock (_gate)
        {
            if (!_finished) { _cleanup.Push(cleanup); return; }
        }
        // 例如后台封面任务迟到创建的 HTTP 客户端，不留在已经排空的清理队列中。
        _ = CleanupAsync(cleanup);
    }
    public void HostStarted(Func<Task> stopHost) => _stopHost = stopHost;

    public async Task<bool> ShutdownAsync()
    {
        if (!lifecycle.TryBeginExit(out bool wasReady)) return false;
        if (wasReady)
            foreach (var save in _save)
                try { await save(); }
                catch (Exception ex) { logger.LogError(ex, "退出时保存状态失败"); }
        if (_stopHost is not null)
            try { await _stopHost(); }
            catch (Exception ex) { logger.LogError(ex, "停止 Host 失败"); }
        while (true)
        {
            Func<Task> cleanup;
            lock (_gate)
            {
                if (!_cleanup.TryPop(out cleanup!)) { _finished = true; break; }
            }
            await CleanupAsync(cleanup);
        }
        logger.LogInformation("应用程序退出完成");
        return true;
    }
    private async Task CleanupAsync(Func<Task> cleanup)
    {
        try { await cleanup(); }
        catch (Exception ex) { logger.LogError(ex, "退出时资源清理失败"); }
    }
}
