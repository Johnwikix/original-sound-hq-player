using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace WinUIMusicPlayer.Services;

/// <summary>仅保留已打开编辑器的退出屏障。控件卸载时注销，避免根容器永久保留临时 VM。</summary>
public sealed class EditorSessions
{
    private readonly HashSet<Session> _active = [];
    private bool _stopping;
    public IDisposable Attach(Func<Task> stop)
    {
        if (_stopping) throw new InvalidOperationException("Editors cannot open during shutdown.");
        var session = new Session(this, stop);
        _active.Add(session);
        return session;
    }
    public async Task StopAsync()
    {
        if (_stopping) return;
        _stopping = true;
        var active = new Session[_active.Count];
        _active.CopyTo(active);
        List<Exception>? failures = null;
        foreach (var session in active)
        {
            try { await session.Stop(); }
            catch (Exception ex) { (failures ??= []).Add(ex); }
            finally { session.Dispose(); }
        }
        if (failures is not null) throw new AggregateException(failures);
    }
    private sealed class Session(EditorSessions owner, Func<Task> stop) : IDisposable
    {
        public Task Stop() => stop();
        public void Dispose() => owner._active.Remove(this);
    }
}
