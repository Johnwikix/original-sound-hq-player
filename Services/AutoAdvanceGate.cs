namespace WinUIMusicPlayer.Services;

/// <summary>自动切歌执行中保留一次新的结束请求；仅更新已有布尔状态，不为每次通知分配队列节点。</summary>
internal sealed class AutoAdvanceGate
{
    private readonly object _sync = new();
    private bool _running;
    private bool _pending;

    public bool Request()
    {
        lock (_sync)
        {
            if (_running)
            {
                _pending = true;
                return false;
            }
            _running = true;
            return true;
        }
    }

    public bool Complete()
    {
        lock (_sync)
        {
            if (_pending)
            {
                _pending = false;
                return true;
            }
            _running = false;
            return false;
        }
    }

    public void Cancel()
    {
        lock (_sync)
        {
            _pending = false;
            _running = false;
        }
    }
}
