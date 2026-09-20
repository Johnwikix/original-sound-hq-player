using System;
using System.Collections.Generic;
using System.Threading;

namespace WinUIMusicPlayer.Services;

public enum AppPhase { Starting, WaitingForAgreement, Initializing, Ready, Stopping, Failed }

/// <summary>单一生命周期状态源；协议记录独立保存，服务构造不改变阶段。</summary>
public sealed class AppLifecycle
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();
    private AppPhase _phase;
    public AppPhase Phase { get { lock (_gate) return _phase; } }
    public bool IsReady => Phase == AppPhase.Ready;
    public CancellationToken StoppingToken => _stopping.Token;
    public event EventHandler? Changed;

    public void TransitionTo(AppPhase next)
    {
        lock (_gate)
        {
            if (_phase == next) return;
            if (_phase == AppPhase.Stopping) throw new OperationCanceledException("Application is stopping.");
            bool valid = next == AppPhase.Failed || (_phase, next) is
                (AppPhase.Starting, AppPhase.WaitingForAgreement) or
                (AppPhase.Starting, AppPhase.Initializing) or
                (AppPhase.WaitingForAgreement, AppPhase.Initializing) or
                (AppPhase.Initializing, AppPhase.Ready);
            if (!valid) throw new InvalidOperationException($"Invalid application transition: {_phase} -> {next}");
            _phase = next;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool TryBeginExit(out bool wasReady)
    {
        lock (_gate)
        {
            wasReady = _phase == AppPhase.Ready;
            if (_phase == AppPhase.Stopping) return false;
            _phase = AppPhase.Stopping;
        }
        List<Exception>? failures = null;
        try
        {
            _stopping.Cancel();
        }
        catch (Exception ex)
        {
            (failures ??= []).Add(ex);
        }
        // 退出是冷路径；逐个通知，避免一个失败订阅者阻止其他入口关闭。
        if (Changed is { } changed)
            foreach (EventHandler handler in changed.GetInvocationList())
                try { handler(this, EventArgs.Empty); }
                catch (Exception ex) { (failures ??= []).Add(ex); }
        if (failures is not null) throw new AggregateException("Application stopping notifications failed.", failures);
        return true;
    }
}
