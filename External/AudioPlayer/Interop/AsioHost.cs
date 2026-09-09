using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Playback;
namespace AudioPlayer.Interop;

/// <summary>
/// ASIO 输出宿主（ECHO asio_host.cpp 移植）：驱动加载、缓冲候选、采样率中转、
/// PCM / DoP / Native DSD 三种渲染路径、隐藏消息窗口（部分驱动需要）。
/// 渲染回调发生在驱动线程：只调用 IRenderSource（内部无锁、预分配）与本类的
/// 预分配 scratch，异常时输出静音并标记失败（ECHO render guard）。
/// </summary>
internal sealed unsafe class AsioOutput : IAudioOutput, IDisposable
{
    private AsioDriver? _driver;
    private Thread? _windowThread;
    private IntPtr _hwnd;
    private readonly ManualResetEventSlim _windowReady = new(false);

    private AsioBufferInfo[] _bufferInfos = [];
    private AsioChannelInfo[] _channelInfos = [];
    private int _outputChannelOffset;
    private int _outputChannelCount;
    private int _bufferSize;
    private bool _postOutput;
    private bool _nativeDsdApplied;
    private int _dsdRateDomain; // NativeDSD：驱动接受的采样率（位率或字节率，厂商各异）
    private bool _started;
    private int _failed; // Interlocked

    // 渲染 scratch（预分配，渲染线程独占）
    private double[] _pcmScratch = [];
    private uint[] _dopScratch = [];
    private byte[] _dsdScratch = [];

    private IRenderSource _source = null!;
    private static AsioOutput? _active;
    private void* _callbacksPtr; // ASIO 回调表（持久非托管内存）

    // ── 驱动线程调度：隐藏窗口线程只做消息服务员（泵）；驱动调用（Init/Start/Stop 等）
    //    一律在调用方线程直接执行——多数驱动内部会向 hwnd PostMessage 并同步等待
    //    （FiiO 实测），调用方线程 ≠ 窗口线程时泵才空闲应答；若把驱动调用经 WM_APP
    //    路由到窗口线程执行，泵被自身占用 = 自死锁。 ──
    private const uint WmAppWork = 0x0401;
    private sealed class WorkItem
    {
        public Action Body = () => { };
        public ManualResetEventSlim Done = new(false);
        public Exception? Error;
    }
    private readonly ConcurrentQueue<WorkItem> _workQueue = new();

    // 仅用于驱动对象创建：此刻驱动尚未拿到 hwnd（Init 才传入），不会同步等待窗口消息
    private bool RunOnWindowThread(Action body, int timeoutMs = 10000)
    {
        if (_hwnd == IntPtr.Zero) return false;
        var item = new WorkItem { Body = body };
        _workQueue.Enqueue(item);
        bool posted = Win32.PostMessageW(_hwnd, WmAppWork, 0, 0);
        if (!posted) Console.WriteLine($"[asio] PostMessage failed err={Win32.GetLastError()}");
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
    }

    // [UnmanagedCallersOnly] 回调（CreateBuffers 注册给驱动）
    private static readonly delegate* unmanaged[Stdcall]<void*, int, int, void*> BufferSwitchTimeInfoPtr = &OnBufferSwitchTimeInfo;
    private static readonly delegate* unmanaged[Stdcall]<int, int, void*, double*, int> AsioMessagePtr = &OnAsioMessage;
    private static readonly delegate* unmanaged[Stdcall]<double, void> SampleRateChangedPtr = &OnSampleRateChanged;
    private static readonly delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint> WndProcPtr = &WndProc;

    public bool Start(int driverIndex, int requestedBufferFrames, IRenderSource source)
    {
        _source = source;
        var drivers = Win32.EnumerateAsioDrivers();
        if (driverIndex < 0 || driverIndex >= drivers.Count) return false;

        Console.WriteLine($"[asio] 可用驱动数={drivers.Count}: {string.Join(" | ", drivers.Select(d => d.Name))}");
        // 驱动调用在调用方线程执行；隐藏窗口线程只做消息服务员：
        // 驱动 Init/Start 期间会向 hwnd 发消息（FiiO 实测同步等待），
        // 调用方线程 ≠ 窗口线程时泵才能应答，同线程调用 = 自死锁
        if (!StartWindowThread()) return false;
        return StartCore(drivers[driverIndex].Clsid, requestedBufferFrames, source);
    }

    private bool StartCore(Guid clsid, int requestedBufferFrames, IRenderSource source)
    {
        try
        {
            if (!RunOnWindowThread(() => _driver = AsioDriver.Create(clsid), 8000))
            {
                Console.WriteLine("[asio] Create timed out");
                return false;
            }
            if (_driver == null) { Console.WriteLine("[asio] Create failed"); return false; }
            Console.WriteLine($"[asio] Create ok on STA");
            int initRet = -1;
            var driver = _driver; // 固定本地引用：超时后字段被置 null，worker 仍持有原对象
            var initWorker = new Thread(() => { initRet = driver!.Init(_hwnd); }, 3 * 1024 * 1024)
            { IsBackground = true, Name = "asio-init" };
            initWorker.Start();
            if (!initWorker.Join(3000))
            {
                Console.WriteLine("[asio] Init timed out (驱动挂死或弹出模态框)");
                // 墓园语义：worker 可能仍阻塞在驱动内部，Release 在途使用的 COM 对象会
                // use-after-free（WASAPI _initTimedOut 同款）。只丢引用，泄漏给进程退出回收
                _driver = null;
                return false;
            }
            if (initRet == 0) { Console.WriteLine("[asio] Init failed"); return false; }
            Console.WriteLine("[asio] init ok");

            if (source.Kind == RenderKind.NativeDsd && !EnableNativeDsdFormat()) return false;

            if (_driver.GetChannels(out int inCh, out int outCh) != AsioConstants.AseOk || outCh <= 0)
            {
                Console.WriteLine($"[asio] GetChannels failed in={inCh} out={outCh}");
                return false;
            }
            _outputChannelCount = Math.Min(outCh, Math.Max(1, source.Channels));

            if (_driver.GetBufferSize(out int minSize, out int maxSize, out int preferred, out int granularity) != AsioConstants.AseOk)
            {
                Console.WriteLine("[asio] GetBufferSize failed");
                return false;
            }
            Console.WriteLine($"[asio] ch in={inCh} out={outCh} buffer min={minSize} max={maxSize} pref={preferred} gran={granularity} rate={source.SampleRate}");
            // DSD 模式下驱动采样率域没有统一规范（有的收位率 2822400，有的收字节率 352800）：
            // 依次尝试；失败即放弃（引擎会回退 ASIO DoP / 共享 PCM）
            double[] rateCandidates = source.Kind == RenderKind.NativeDsd
                ? new[] { (double)source.SampleRate, source.SampleRate / 8.0 }
                : new[] { (double)source.SampleRate };
            int srr = -1;
            foreach (double rate in rateCandidates)
            {
                srr = SetSampleRateAndWait(rate);
                if (srr == AsioConstants.AseOk)
                {
                    _dsdRateDomain = (int)Math.Round(rate);
                    break;
                }
                Console.WriteLine($"[asio] SetSampleRate({rate:0.##}) failed ret={srr}");
            }
            if (srr != AsioConstants.AseOk) return false;

            int wanted = requestedBufferFrames > 0 ? requestedBufferFrames : preferred;
            bool created = false;
            foreach (int candidate in BuildBufferCandidates(minSize, maxSize, preferred, granularity, wanted))
            {
                if (TryCreateBuffers(candidate, includeInputs: false)) { created = true; break; }
                if (TryCreateBuffers(candidate, includeInputs: true)) { created = true; break; }
            }
            if (!created) return false;

            // 渲染资源
            if (source.Kind == RenderKind.Pcm) _pcmScratch = new double[_bufferSize * source.Channels];
            else if (source.Kind == RenderKind.Dop) _dopScratch = new uint[_bufferSize * source.Channels];
            else _dsdScratch = new byte[((_bufferSize + 7) / 8 + 1) * source.Channels];

            _active = this;
            WriteSilence(0);
            WriteSilence(1);
            int startRet = _driver.Start();
            if (startRet != AsioConstants.AseOk)
            {
                Console.WriteLine($"[asio] Start failed ret={startRet}");
                _active = null;
                return false;
            }
            _started = true;
            var firstInfo = _channelInfos.Length > _outputChannelOffset ? (AsioChannelInfo?)_channelInfos[_outputChannelOffset] : null;
            string chName = firstInfo != null ? FixedName(firstInfo.Value) : "";
            Console.WriteLine($"[asio] started buffer={_bufferSize} type={(firstInfo != null ? firstInfo.Value.Type : -1)}" +
                $" group={(firstInfo != null ? firstInfo.Value.ChannelGroup : -1)} name=\"{chName}\" latency={LatencyMs}ms");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Pause()
    {
        // 直调（调用方线程）是有意的：窗口线程必须保持空闲以应答驱动 Stop 期间的
        // 同步等待（见类头“驱动线程调度”），路由到窗口线程反而自死锁
        if (_started && _driver != null) { try { _driver.Stop(); } catch { } }
    }

    public void Resume()
    {
        if (_started && _driver != null) { try { _driver.Start(); } catch { } }
    }

    public bool IsFailed => Volatile.Read(ref _failed) != 0;

    public int LatencyMs { get; private set; }

    // ─────────────── 初始化细节（ECHO 移植） ───────────────

    private bool TryCreateBuffers(int bufferSize, bool includeInputs)
    {
        if (_driver == null) return false;
        var infos = new List<AsioBufferInfo>();
        if (includeInputs)
        {
            _driver.GetChannels(out int inCh, out _);
            for (int i = 0; i < inCh; i++)
                infos.Add(new AsioBufferInfo { IsInput = 1, ChannelNum = i });
        }
        _outputChannelOffset = infos.Count;
        for (int i = 0; i < _outputChannelCount; i++)
            infos.Add(new AsioBufferInfo { IsInput = 0, ChannelNum = i });

        var bufferArray = infos.ToArray();
        // 回调指针表必须放在持久非托管内存：驱动长期持有该地址，栈上 fixed 的地址
        // 在本方法返回后失效，ASIOStart 后首个 bufferSwitch 回调即跳垃圾地址 → 闪退
        _callbacksPtr = NativeMemory.Alloc((nuint)sizeof(AsioCallbacks));
        ((AsioCallbacks*)_callbacksPtr)->BufferSwitchTimeInfo = (IntPtr)BufferSwitchTimeInfoPtr;
        ((AsioCallbacks*)_callbacksPtr)->AsioMessage = (IntPtr)AsioMessagePtr;
        ((AsioCallbacks*)_callbacksPtr)->SampleRateDidChange = (IntPtr)SampleRateChangedPtr;
        bool ok;
        fixed (AsioBufferInfo* pInfos = bufferArray)
        {
            ok = _driver.CreateBuffers(pInfos, bufferArray.Length, bufferSize, (AsioCallbacks*)_callbacksPtr) == AsioConstants.AseOk;
        }
        if (!ok)
        {
            try { _driver.DisposeBuffers(); } catch { }
            return false;
        }

        // 校验声道采样类型（失败必须 DisposeBuffers，否则缓冲残留导致后续候选全部失败）
        var channelInfos = new AsioChannelInfo[bufferArray.Length];
        for (int i = 0; i < bufferArray.Length; i++)
        {
            channelInfos[i] = new AsioChannelInfo { Channel = bufferArray[i].ChannelNum, IsInput = bufferArray[i].IsInput };
            if (_driver.GetChannelInfo(ref channelInfos[i]) != AsioConstants.AseOk
                || (bufferArray[i].IsInput == 0 && !SampleTypeSupported(channelInfos[i].Type, _source.Kind)))
            {
                try { _driver.DisposeBuffers(); } catch { }
                return false;
            }
        }

        _bufferInfos = bufferArray;
        _channelInfos = channelInfos;
        _bufferSize = bufferSize;
        LatencyMs = (int)(_bufferSize * 2L * 1000 / Math.Max(1, _source.SampleRate)); // 双缓冲
        _postOutput = true; // 直接调用，驱动不支持时返回错误码无害
        return true;
    }

    private static bool SampleTypeSupported(int type, RenderKind kind) => kind switch
    {
        RenderKind.Pcm => type is AsioConstants.AsioStInt16Lsb or AsioConstants.AsioStInt24Lsb
            or AsioConstants.AsioStInt32Lsb or AsioConstants.AsioStFloat32Lsb or AsioConstants.AsioStFloat64Lsb
            or AsioConstants.AsioStInt32Lsb16 or AsioConstants.AsioStInt32Lsb18
            or AsioConstants.AsioStInt32Lsb20 or AsioConstants.AsioStInt32Lsb24
            or AsioConstants.AsioStInt16Msb or AsioConstants.AsioStInt24Msb
            or AsioConstants.AsioStInt32Msb or AsioConstants.AsioStFloat32Msb or AsioConstants.AsioStFloat64Msb
            or AsioConstants.AsioStInt32Msb16 or AsioConstants.AsioStInt32Msb18
            or AsioConstants.AsioStInt32Msb20 or AsioConstants.AsioStInt32Msb24,
        RenderKind.Dop => type is AsioConstants.AsioStInt24Lsb or AsioConstants.AsioStInt24Msb
            or AsioConstants.AsioStInt32Lsb24 or AsioConstants.AsioStInt32Msb24
            or AsioConstants.AsioStInt32Lsb or AsioConstants.AsioStInt32Msb,
        _ => type is AsioConstants.AsioStDsdInt8Lsb1 or AsioConstants.AsioStDsdInt8Msb1 or AsioConstants.AsioStDsdInt8Ner8,
    };

    private bool EnableNativeDsdFormat()
    {
        if (_driver == null) return false;
        // ASIOIoFormat：long FormatType + char future[508]，共 512 字节（驱动可能回写 -1）。
        // FiiO/Thesycon 的 CanDo/Set 返回私有魔数（0x3F4847A0，非 ASE_SUCCESS）——
        // 按 SDK 语义判据：ASE 错误为负值；支持则 FormatType 保持不变，失败被改为 -1。
        // 切换是否真生效由后续通道类型校验兜底（非 DSD 类型 → TryCreateBuffers 失败回退）。
        byte* format = stackalloc byte[512];
        ZeroMemory(format, 512);
        *(int*)format = AsioConstants.KAsioDsdFormat;
        int can = _driver.Future(AsioConstants.KAsioCanDoIoFormat, format);
        Console.WriteLine($"[asio] CanDoIoFormat(DSD) ret=0x{can:X8} FormatType={*(int*)format}");
        if (can < 0 || *(int*)format != AsioConstants.KAsioDsdFormat) return false;
        ZeroMemory(format, 512);
        *(int*)format = AsioConstants.KAsioDsdFormat;
        int set = _driver.Future(AsioConstants.KAsioSetIoFormat, format);
        Console.WriteLine($"[asio] SetIoFormat(DSD) ret=0x{set:X8} FormatType={*(int*)format}");
        if (set < 0 || *(int*)format != AsioConstants.KAsioDsdFormat) return false;
        _nativeDsdApplied = true;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ZeroMemory(byte* p, int len)
    {
        for (int i = 0; i < len; i++) p[i] = 0;
    }

    private static string FixedName(in AsioChannelInfo info)
    {
        fixed (byte* p = info.Name)
        {
            int len = 0;
            while (len < 32 && p[len] != 0) len++;
            return len == 0 ? "" : System.Text.Encoding.ASCII.GetString(p, len);
        }
    }

    private int SetSampleRateAndWait(double requested)
    {
        if (_driver == null) return -1;
        if (_driver.CanSampleRate(requested) != AsioConstants.AseOk) return -1000;
        if (_driver.SetSampleRate(requested) != AsioConstants.AseOk) return -1001;
        double observed = WaitForRate(requested, 25, 20);
        if (!RateMatches(observed, requested))
        {
            foreach (double pivot in PivotCandidates(requested))
            {
                if (TryRatePivot(pivot, requested, out observed)) break;
            }
        }
        return RateMatches(observed, requested) ? AsioConstants.AseOk : -1002;
    }

    private double ReadRateOr(double fallback)
    {
        double rate = fallback;
        if (_driver == null || _driver.GetSampleRate(out double actual) != AsioConstants.AseOk || actual <= 0) return fallback;
        return actual;
    }

    private double WaitForRate(double requested, int attempts, int sleepMs)
    {
        double observed = ReadRateOr(requested);
        for (int i = 0; i < attempts && !RateMatches(observed, requested); i++)
        {
            Thread.Sleep(Math.Max(1, sleepMs));
            observed = ReadRateOr(observed);
        }
        return observed;
    }

    private bool TryRatePivot(double pivot, double requested, out double actualRate)
    {
        actualRate = requested;
        if (_driver == null || RateMatches(pivot, requested) || _driver.CanSampleRate(pivot) != AsioConstants.AseOk)
            return false;
        if (_driver.SetSampleRate(pivot) != AsioConstants.AseOk) return false;
        double pivotObserved = WaitForRate(pivot, 20, 20);
        Thread.Sleep(50);
        if (_driver.SetSampleRate(requested) != AsioConstants.AseOk)
        {
            actualRate = ReadRateOr(pivotObserved);
            return false;
        }
        actualRate = WaitForRate(requested, 35, 20);
        return RateMatches(actualRate, requested);
    }

    private static bool RateMatches(double a, double b) => Math.Abs(a - b) < 0.5;

    private static double[] PivotCandidates(double requested)
    {
        double[] known = { 44100, 48000, 88200, 96000, 176400, 192000 };
        var list = new List<double>();
        void Add(double r)
        {
            if (RateMatches(r, requested)) return;
            if (!list.Contains(r)) list.Add(r);
        }
        if (!RateMatches(requested, 48000)) Add(48000);
        foreach (double r in known) Add(r);
        return list.ToArray();
    }

    private static bool IsPowerOfTwo(int v) => v > 0 && (v & (v - 1)) == 0;

    private static bool BufferSizeLegal(int size, int min, int max, int preferred, int granularity)
    {
        if (size <= 0) return false;
        if (min <= 0) min = 1;
        if (max < min) max = Math.Max(min, preferred);
        if (size < min || size > max) return false;
        if (size == preferred) return true;
        if (granularity == -1) return IsPowerOfTwo(size);
        if (granularity > 0) return (size - min) % granularity == 0;
        return true;
    }

    private static void AddCandidate(List<int> list, int size, int min, int max, int preferred, int granularity)
    {
        if (BufferSizeLegal(size, min, max, preferred, granularity) && !list.Contains(size)) list.Add(size);
    }

    private static void AddNearestCandidates(List<int> list, int size, int min, int max, int preferred, int granularity)
    {
        if (size <= 0) return;
        AddCandidate(list, size, min, max, preferred, granularity);
        if (min <= 0) min = 1;
        if (max < min) max = Math.Max(min, preferred);
        int clamped = Math.Max(min, Math.Min(max, size));
        AddCandidate(list, clamped, min, max, preferred, granularity);
        if (granularity == -1)
        {
            int lower = 1;
            while (lower <= clamped / 2) lower *= 2;
            int upper = lower;
            while (upper < clamped && upper <= max / 2) upper *= 2;
            AddCandidate(list, lower, min, max, preferred, granularity);
            AddCandidate(list, upper, min, max, preferred, granularity);
            if (upper <= max / 2) AddCandidate(list, upper * 2, min, max, preferred, granularity);
            return;
        }
        if (granularity > 0)
        {
            int offset = clamped - min;
            int lower = min + offset / granularity * granularity;
            int upper = lower + granularity;
            AddCandidate(list, lower, min, max, preferred, granularity);
            AddCandidate(list, upper, min, max, preferred, granularity);
        }
    }

    private static int[] BuildBufferCandidates(int min, int max, int preferred, int granularity, int requested)
    {
        var list = new List<int>();
        if (requested > 0) AddNearestCandidates(list, requested, min, max, preferred, granularity);
        AddCandidate(list, preferred, min, max, preferred, granularity);
        foreach (int size in new[] { 512, 1024, 2048, 4096, 8192, 256 })
            AddNearestCandidates(list, size, min, max, preferred, granularity);
        AddCandidate(list, min, min, max, preferred, granularity);
        AddCandidate(list, max, min, max, preferred, granularity);
        return list.ToArray();
    }

    // ─────────────── 消息窗口 ───────────────

    private bool StartWindowThread()
    {
        _windowThread = new Thread(WindowThreadProc) { IsBackground = true, Name = "asio-window" };
        _windowThread.Start();
        return _windowReady.Wait(3000);
    }

    private void WindowThreadProc(object? obj)
    {
        Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_APARTMENTTHREADED);
        try
        {
            var wc = new Win32.WNDCLASSW
            {
                lpfnWndProc = (IntPtr)WndProcPtr,
                hInstance = Win32.GetModuleHandleW(IntPtr.Zero),
                lpszClassName = "AudioPlayerAsioWindow",
            };
            Win32.RegisterClassW(ref wc);
            _hwnd = Win32.CreateWindowExW(0, "AudioPlayerAsioWindow", "AudioPlayer ASIO Host",
                0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
            Console.WriteLine($"[asio] window created hwnd=0x{_hwnd:X}");
            _windowReady.Set();
            if (_hwnd == IntPtr.Zero) return;
            Console.WriteLine("[asio] pump started");
            int seen = 0;
            try
            {
                while (true)
                {
                    if (!Win32.GetMessageW(out Win32.MSG msg, IntPtr.Zero, 0, 0))
                    {
                        Console.WriteLine($"[asio] pump exit (WM_QUIT 或错误) msg={msg.message}");
                        break;
                    }
                    if (seen++ < 10) Console.WriteLine($"[asio] msg {msg.message}");
                    if (msg.message == WmAppWork) { Console.WriteLine("[asio] work received"); DrainWorkQueue(); }
                    Win32.DispatchMessageW(ref msg);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[asio] pump crashed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch { }
        finally
        {
            Win32.CoUninitialize();
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static nint WndProc(IntPtr hwnd, uint msg, nuint wParam, nint lParam)
    {
        if (msg == 2 /*WM_DESTROY*/) Win32.PostQuitMessage(0);
        return Win32.DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    // ─────────────── 驱动回调 ───────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void* OnBufferSwitchTimeInfo(void* timeInfo, int bufferIndex, int directProcess)
    {
        RenderSafely(bufferIndex);
        return timeInfo;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static void OnSampleRateChanged(double sampleRate) { }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnAsioMessage(int selector, int value, void* message, double* opt)
    {
        return selector switch
        {
            AsioConstants.KAsioSelectorSupported => value is AsioConstants.KAsioResetRequest
                or AsioConstants.KAsioEngineVersion or AsioConstants.KAsioResyncRequest
                or AsioConstants.KAsioLatenciesChanged or AsioConstants.KAsioSupportsTimeInfo
                or AsioConstants.KAsioSupportsTimeCode or AsioConstants.KAsioSupportsInputMonitor ? 1 : 0,
            AsioConstants.KAsioResetRequest or AsioConstants.KAsioResyncRequest
                or AsioConstants.KAsioLatenciesChanged => 1,
            AsioConstants.KAsioEngineVersion => 2,
            AsioConstants.KAsioSupportsTimeInfo => 1,
            _ => 0,
        };
    }

    private static void RenderSafely(int bufferIndex)
    {
        var host = Volatile.Read(ref _active);
        if (host == null) return;
        try
        {
            host.Render(bufferIndex);
        }
        catch
        {
            Interlocked.Exchange(ref host._failed, 1);
            try { host.WriteSilence(bufferIndex); } catch { }
        }
    }

    // ─────────────── 渲染 ───────────────

    private void Render(int bufferIndex)
    {
        switch (_source.Kind)
        {
            case RenderKind.Pcm:
            {
                _source.FillPcm(_pcmScratch, _bufferSize); // 环渲染先写整块静音，无需外部清零
                for (int ch = 0; ch < _outputChannelCount; ch++)
                {
                    int idx = _outputChannelOffset + ch;
                    WritePcmChannel(_bufferInfos[idx].GetBuffer(bufferIndex), _channelInfos[idx].Type, ch, _bufferSize);
                }
                break;
            }
            case RenderKind.Dop:
            {
                _source.FillDop(_dopScratch, _bufferSize);
                for (int ch = 0; ch < _outputChannelCount; ch++)
                {
                    int idx = _outputChannelOffset + ch;
                    WriteDopChannel(_bufferInfos[idx].GetBuffer(bufferIndex), _channelInfos[idx].Type, ch, _bufferSize);
                }
                break;
            }
            default:
            {
                int byteFrames = (_bufferSize + 7) / 8;
                _source.FillDsdBytes(_dsdScratch, byteFrames);
                for (int ch = 0; ch < _outputChannelCount; ch++)
                {
                    int idx = _outputChannelOffset + ch;
                    WriteNativeDsdChannel(_bufferInfos[idx].GetBuffer(bufferIndex), _channelInfos[idx].Type,
                        byteFrames, ch);
                }
                break;
            }
        }
        if (Diagnostics.BufferDump.Enabled && _outputChannelCount > 0)
        {
            int idx = _outputChannelOffset;
            int type = _channelInfos[idx].Type;
            int samples = _source.Kind == RenderKind.NativeDsd
                ? (type == AsioConstants.AsioStDsdInt8Ner8 ? _bufferSize : (_bufferSize + 7) / 8) // LSB1/MSB1：位域缓冲
                : _bufferSize;
            Diagnostics.BufferDump.Write((byte*)_bufferInfos[idx].GetBuffer(bufferIndex),
                samples * AsioBytesPerSample(type, _source.Kind));
        }
        if (_postOutput && _driver != null) _driver.OutputReady();
    }

    private static int AsioBytesPerSample(int type, RenderKind kind)
    {
        if (kind == RenderKind.NativeDsd) return 1; // LSB1/MSB1=字节缓冲（帧域为位）；NER8 逐位输出
        return type switch
        {
            AsioConstants.AsioStInt16Lsb or AsioConstants.AsioStInt16Msb => 2,
            AsioConstants.AsioStInt24Lsb or AsioConstants.AsioStInt24Msb => 3,
            AsioConstants.AsioStFloat64Lsb or AsioConstants.AsioStFloat64Msb => 8,
            _ => 4,
        };
    }

    private void WritePcmChannel(IntPtr buffer, int type, int channel, int frames)
    {
        int channels = _source.Channels;
        byte* dst = (byte*)buffer;
        for (int f = 0; f < frames; f++)
        {
            double sample = _pcmScratch[f * channels + Math.Min(channel, channels - 1)];
            WriteAsioSample(dst, type, f, sample);
        }
    }

    private void WriteDopChannel(IntPtr buffer, int type, int channel, int frames)
    {
        int channels = _source.Channels;
        byte* dst = (byte*)buffer;
        for (int f = 0; f < frames; f++)
        {
            uint sample = _dopScratch[f * channels + Math.Min(channel, channels - 1)];
            WriteAsioDopSample(dst, type, f, sample);
        }
    }

    private void WriteNativeDsdChannel(IntPtr buffer, int type, int byteFrames, int channel)
    {
        int channels = _source.Channels;
        int srcChannel = Math.Min(channel, channels - 1);
        byte* dst = (byte*)buffer;
        WriteNativeDsd(dst, type, byteFrames, _dsdScratch, channels, srcChannel);
    }

    private void WriteSilence(int bufferIndex)
    {
        for (int ch = 0; ch < _outputChannelCount; ch++)
        {
            int idx = _outputChannelOffset + ch;
            IntPtr buf = _bufferInfos[idx].GetBuffer(bufferIndex);
            if (buf == IntPtr.Zero) continue;
            int type = _channelInfos[idx].Type;
            byte* dst = (byte*)buf;
            switch (_source.Kind)
            {
                case RenderKind.Pcm:
                    for (int f = 0; f < _bufferSize; f++) WriteAsioSample(dst, type, f, 0.0);
                    break;
                case RenderKind.Dop:
                    if (_dopScratch.Length >= _bufferSize * _source.Channels)
                    {
                        // 走环渲染：标记相位由 DopRing 全局帧计数保证连续（静音也不例外）
                        _source.FillDop(_dopScratch, _bufferSize);
                        WriteDopChannel(buf, type, ch, _bufferSize);
                        continue;
                    }
                    for (int f = 0; f < _bufferSize; f++) WriteAsioDopSample(dst, type, f, (f & 1) == 0 ? 0x050000u : 0xfa0000u);
                    break;
                default:
                    WriteNativeDsd(dst, type, (_bufferSize + 7) / 8, null, 0, 0);
                    break;
            }
        }
        if (_postOutput && _driver != null && _started) _driver.OutputReady();
    }

    // ─────────────── 采样写入（ECHO 移植） ───────────────

    private static void WriteU16Be(byte* p, ushort v) { p[0] = (byte)(v >> 8); p[1] = (byte)v; }
    private static void WriteU24Be(byte* p, int v) { p[0] = (byte)((v >> 16) & 0xff); p[1] = (byte)((v >> 8) & 0xff); p[2] = (byte)(v & 0xff); }
    private static void WriteU24Le(byte* p, int v) { p[0] = (byte)(v & 0xff); p[1] = (byte)((v >> 8) & 0xff); p[2] = (byte)((v >> 16) & 0xff); }
    private static void WriteU32Be(byte* p, uint v) { p[0] = (byte)(v >> 24); p[1] = (byte)(v >> 16); p[2] = (byte)(v >> 8); p[3] = (byte)v; }

    private static double ClampSample(double s) => s > 1.0 ? 1.0 : s < -1.0 ? -1.0 : s;

    private static int ScaledInt(double sample, int bits)
    {
        double max = bits switch { 16 => 32767.0, 24 => 8388607.0, _ => 2147483647.0 };
        return (int)(ClampSample(sample) * max);
    }

    private static int AlignedI32(double sample, int validBits)
    {
        int v = ScaledInt(sample, validBits);
        return validBits == 32 ? v : v << (32 - validBits);
    }

    private static void WriteAsioSample(byte* buffer, int type, int frame, double sample)
    {
        switch (type)
        {
            case AsioConstants.AsioStInt16Lsb: ((short*)buffer)[frame] = (short)ScaledInt(sample, 16); break;
            case AsioConstants.AsioStInt16Msb: WriteU16Be(buffer + frame * 2, (ushort)(short)ScaledInt(sample, 16)); break;
            case AsioConstants.AsioStInt24Lsb: WriteU24Le(buffer + frame * 3, ScaledInt(sample, 24)); break;
            case AsioConstants.AsioStInt24Msb: WriteU24Be(buffer + frame * 3, ScaledInt(sample, 24)); break;
            case AsioConstants.AsioStInt32Lsb: ((int*)buffer)[frame] = ScaledInt(sample, 32); break;
            case AsioConstants.AsioStInt32Msb: WriteU32Be(buffer + frame * 4, (uint)ScaledInt(sample, 32)); break;
            case AsioConstants.AsioStInt32Lsb16: ((int*)buffer)[frame] = AlignedI32(sample, 16); break;
            case AsioConstants.AsioStInt32Lsb18: ((int*)buffer)[frame] = AlignedI32(sample, 18); break;
            case AsioConstants.AsioStInt32Lsb20: ((int*)buffer)[frame] = AlignedI32(sample, 20); break;
            case AsioConstants.AsioStInt32Lsb24: ((int*)buffer)[frame] = AlignedI32(sample, 24); break;
            case AsioConstants.AsioStInt32Msb16: WriteU32Be(buffer + frame * 4, (uint)AlignedI32(sample, 16)); break;
            case AsioConstants.AsioStInt32Msb18: WriteU32Be(buffer + frame * 4, (uint)AlignedI32(sample, 18)); break;
            case AsioConstants.AsioStInt32Msb20: WriteU32Be(buffer + frame * 4, (uint)AlignedI32(sample, 20)); break;
            case AsioConstants.AsioStInt32Msb24: WriteU32Be(buffer + frame * 4, (uint)AlignedI32(sample, 24)); break;
            case AsioConstants.AsioStFloat32Lsb: ((float*)buffer)[frame] = (float)ClampSample(sample); break;
            case AsioConstants.AsioStFloat32Msb:
                float f = (float)ClampSample(sample);
                WriteU32Be(buffer + frame * 4, *(uint*)&f);
                break;
            case AsioConstants.AsioStFloat64Lsb: ((double*)buffer)[frame] = ClampSample(sample); break;
            case AsioConstants.AsioStFloat64Msb:
                double d = ClampSample(sample);
                ulong u = *(ulong*)&d;
                byte* t = buffer + frame * 8;
                t[0] = (byte)(u >> 56); t[1] = (byte)(u >> 48); t[2] = (byte)(u >> 40); t[3] = (byte)(u >> 32);
                t[4] = (byte)(u >> 24); t[5] = (byte)(u >> 16); t[6] = (byte)(u >> 8); t[7] = (byte)u;
                break;
        }
    }

    private static void WriteAsioDopSample(byte* buffer, int type, int frame, uint sample24)
    {
        uint payload = sample24 & 0x00ffffffu;
        byte dsd1 = (byte)(payload & 0xffu);
        byte dsd2 = (byte)((payload >> 8) & 0xffu);
        byte marker = (byte)((payload >> 16) & 0xffu);
        switch (type)
        {
            case AsioConstants.AsioStInt24Lsb:
                buffer[frame * 3 + 0] = marker;
                buffer[frame * 3 + 1] = dsd1;
                buffer[frame * 3 + 2] = dsd2;
                break;
            case AsioConstants.AsioStInt24Msb:
                buffer[frame * 3 + 0] = marker;
                buffer[frame * 3 + 1] = dsd2;
                buffer[frame * 3 + 2] = dsd1;
                break;
            case AsioConstants.AsioStInt32Lsb24:
                ((uint*)buffer)[frame] = payload << 8;
                break;
            case AsioConstants.AsioStInt32Lsb:
                ((uint*)buffer)[frame] = ((uint)marker << 24) | ((uint)marker << 16) | ((uint)dsd1 << 8) | dsd2;
                break;
            case AsioConstants.AsioStInt32Msb24:
            case AsioConstants.AsioStInt32Msb:
                WriteU32Be(buffer + frame * 4, payload << 8);
                break;
        }
    }

    private static byte ReverseBits(byte b)
    {
        b = (byte)(((b & 0xf0) >> 4) | ((b & 0x0f) << 4));
        b = (byte)(((b & 0xcc) >> 2) | ((b & 0x33) << 2));
        b = (byte)(((b & 0xaa) >> 1) | ((b & 0x55) << 1));
        return b;
    }

    /// <summary>原生 DSD 写入。源：交织字节帧、MSB 优先（DsdRawReader repack 约定，与 DoP 数据一致）。
    /// 注意与 ECHO 相反：ECHO 源是 LSB-first 故 MSB1 反转；本引擎源是 MSB-first，
    /// 因此 MSB1（首采样在最高位）直传、LSB1（首采样在最低位）反转。</summary>
    private static void WriteNativeDsd(byte* buffer, int type, int byteFrames, byte[]? source,
        int sourceChannels, int sourceChannel)
    {
        const byte silence = 0x69;
        bool driverMsbFirst = type == AsioConstants.AsioStDsdInt8Msb1;

        if (type == AsioConstants.AsioStDsdInt8Ner8)
        {
            // NER8：每帧 1 位输出
            int frames = byteFrames * 8;
            for (int frame = 0; frame < frames; frame++)
            {
                byte value = silence;
                int sourceByteFrame = frame / 8;
                int sourceBit = 7 - frame % 8; // 源 MSB 优先：第 n 个采样在位 7-n
                if (source != null && sourceChannels > 0 && sourceChannel < sourceChannels && sourceByteFrame < byteFrames)
                    value = source[sourceByteFrame * sourceChannels + sourceChannel];
                buffer[frame] = (byte)((value >> sourceBit) & 0x01);
            }
            return;
        }

        // LSB1 / MSB1：按驱动位序直传或反转
        for (int bf = 0; bf < byteFrames; bf++)
        {
            byte value = silence;
            if (source != null && sourceChannels > 0 && sourceChannel < sourceChannels && bf < byteFrames)
                value = source[bf * sourceChannels + sourceChannel];
            buffer[bf] = driverMsbFirst ? value : ReverseBits(value);
        }
    }

    public void Dispose()
    {
        if (_active == this) Volatile.Write(ref _active, null);
        if (_driver != null)
        {
            try
            {
                if (_started) { try { _driver.Stop(); } catch { } _started = false; }
                try { _driver.DisposeBuffers(); } catch { }
                if (_nativeDsdApplied)
                {
                    byte* format = stackalloc byte[64];
                    ZeroMemory(format, 64);
                    *(int*)format = AsioConstants.KAsioPcmFormat;
                    try { _driver.Future(AsioConstants.KAsioSetIoFormat, format); } catch { }
                }
            }
            catch { }
            _driver.Dispose();
            _driver = null;
        }
        if (_callbacksPtr != null) { NativeMemory.Free(_callbacksPtr); _callbacksPtr = null; }
        if (_hwnd != IntPtr.Zero)
        {
            Win32.PostMessageW(_hwnd, 0x0010 /*WM_CLOSE*/, 0, 0); // 窗口线程亲和：经消息泵销毁
            _hwnd = IntPtr.Zero;
        }
        _windowReady.Dispose();
    }
}

internal static class AsioBufferInfoExtensions
{
    /// <summary>取第 index 个双缓冲指针。</summary>
    public static unsafe IntPtr GetBuffer(this in AsioBufferInfo info, int index)
        => index == 0 ? info.Buffer0 : info.Buffer1;
}
