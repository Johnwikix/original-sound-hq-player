using System;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services;

/// <summary>调用方线程上的合并写入屏障：一个在途任务和一个 dirty 位，不为每次编辑保存快照。</summary>
public sealed class SettingsSaveQueue(Func<Task> write)
{
    private Task _pending = Task.CompletedTask;
    private bool _dirty;

    /// <summary>必须在同一个同步上下文调用；返回值覆盖本次编辑以及写盘期间的新编辑。</summary>
    public Task RequestAsync()
    {
        _dirty = true;
        if (_pending.IsCompleted) _pending = DrainAsync();
        return _pending;
    }

    public Task FlushAsync() => _dirty && _pending.IsCompleted ? RequestAsync() : _pending;

    private async Task DrainAsync()
    {
        // 同一 UI 事件中修改多个偏好只构建一次快照；后续 await 仍回到该上下文取值。
        await Task.Yield();
        while (_dirty)
        {
            _dirty = false;
            try { await write(); }
            catch
            {
                _dirty = true;
                throw;
            }
        }
    }
}
