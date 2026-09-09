import io, sys

def patch(path, pairs):
    s = io.open(path, encoding='utf-8').read()
    for old, new in pairs:
        if old not in s:
            print('MISS: %s' % old[:80].replace('\n', '\\n'))
            sys.exit(1)
        s = s.replace(old, new)
    io.open(path, 'w', encoding='utf-8', newline='').write(s)
    print('patched')

base = r'D:\code\winui\music_player\External\AudioPlayer'
p = base + r'\Interop\AsioHost.cs'

# 1) using + 工作队列基础设施
patch(p, [(
'using System.Runtime.CompilerServices;',
'using System.Collections.Concurrent;\nusing System.Runtime.CompilerServices;')])

patch(p, [(
'''    private IRenderSource _source = null!;
    private static AsioOutput? _active;
    private void* _callbacksPtr; // ASIO 回调表（持久非托管内存）''',
'''    private IRenderSource _source = null!;
    private static AsioOutput? _active;
    private void* _callbacksPtr; // ASIO 回调表（持久非托管内存）

    // ── 驱动线程调度：ASIO 驱动的 Init/Start/Stop 等必须在带消息泵的 STA 线程上
    //    调用（多数驱动内部建窗/PostMessage 并同步等待；MTA 监听线程直调会卡死）。
    //    全部驱动交互经 WM_APP 消息路由到窗口线程执行。 ──
    private const uint WmAppWork = 0x0401;
    private sealed class WorkItem
    {
        public Action Body = () => { };
        public ManualResetEventSlim Done = new(false);
        public Exception? Error;
    }
    private readonly ConcurrentQueue<WorkItem> _workQueue = new();

    private bool RunOnWindowThread(Action body, int timeoutMs = 10000)
    {
        if (_hwnd == IntPtr.Zero) return false;
        var item = new WorkItem { Body = body };
        _workQueue.Enqueue(item);
        Win32.PostMessageW(_hwnd, WmAppWork, 0, 0);
        if (!item.Done.Wait(timeoutMs)) return false; // 驱动卡死：按失败处理（调用方自行回退）
        if (item.Error != null) throw item.Error;
        return true;
    }

    private void DrainWorkQueue()
    {
        while (_workQueue.TryDequeue(out var w))
        {
            try { w.Body(); }
            catch (Exception ex) { w.Error = ex; }
            w.Done.Set();
        }
    }''')])

# 2) 窗口线程处理 WM_APP_WORK
patch(p, [(
'''            while (Win32.GetMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0)) Win32.DispatchMessageW(ref msg);''',
'''            while (Win32.GetMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WmAppWork) DrainWorkQueue();
                Win32.DispatchMessageW(ref msg);
            }''')])

# 3) Start：整个初始化序列移到窗口线程执行
patch(p, [(
'''        _source = source;
        var drivers = Win32.EnumerateAsioDrivers();
        if (driverIndex < 0 || driverIndex >= drivers.Count) return false;

        Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        try
        {
            if (!StartWindowThread()) return false;
            Console.WriteLine($"[asio] driver: {drivers[driverIndex].Name}");''',
'''        _source = source;
        var drivers = Win32.EnumerateAsioDrivers();
        if (driverIndex < 0 || driverIndex >= drivers.Count) return false;

        if (!StartWindowThread()) return false;
        bool ok = false;
        if (!RunOnWindowThread(() => { ok = StartCore(drivers[driverIndex].Clsid, requestedBufferFrames, source); }))
        {
            Console.WriteLine("[asio] init timed out on driver thread");
            return false;
        }
        return ok;
    }

    private bool StartCore(Guid clsid, int requestedBufferFrames, IRenderSource source)
    {
        try
        {
            Console.WriteLine("[asio] init on window thread");''')])

# StartCore 收尾（找到 Start 方法原来的 return true;/catch 对应段）
patch(p, [(
'''            _started = true;
            Console.WriteLine($"[asio] started buffer={_bufferSize} type={(_channelInfos.Length > _outputChannelOffset ? _channelInfos[_outputChannelOffset].Type : -1)} latency={LatencyMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[asio] Start exception: {ex.Message}");
            return false;
        }
    }''',
'''            _started = true;
            Console.WriteLine($"[asio] started buffer={_bufferSize} type={(_channelInfos.Length > _outputChannelOffset ? _channelInfos[_outputChannelOffset].Type : -1)} latency={LatencyMs}ms");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[asio] Start exception: {ex.Message}");
            return false;
        }
    }

    [DllImport("ole32")]
    private static extern int CoInitializeEx(IntPtr p, uint coinit);''')])

# StartCore 里驱动创建改用窗口线程的 COM（移除旧的 MTA 直调）
patch(p, [(
'''            Console.WriteLine("[asio] init on window thread");
            _driver = AsioDriver.Create(drivers[driverIndex].Clsid);''',
'''            Console.WriteLine("[asio] init on window thread");
            _driver = AsioDriver.Create(clsid);''')])
print('ALL OK')
