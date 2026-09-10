using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// Senary Audio 独占模式失效探针：PUSH vs EVENT 对照诊断。
// 实验 A：独占 PUSH，periodicity = bufferHns（引擎现代码行为）
// 实验 B：独占 PUSH，periodicity = 0（官方样例/PortAudio/JUCE 惯例）
// 实验 C：独占 EVENT（SetEventHandle + 整块写），对照
// 所有写入走 ReleaseBuffer(SILENT)，不关心端点采样格式；结论在 API 层：
//  - Initialize hr（periodicity/格式是否被拒）
//  - Start 后 GetCurrentPadding 轨迹（卡死=推送无声根因 / 量化=爆发写入 / 正常）
//  - 任意尺寸 GetBuffer 写入是否被接受、EVENT 是否触发及周期

internal static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEX
    {
        public ushort wFormatTag, nChannels;
        public uint nSamplesPerSec, nAvgBytesPerSec;
        public ushort nBlockAlign, wBitsPerSample, cbSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WAVEFORMATEXTENSIBLE
    {
        public WAVEFORMATEX Format;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropertyKey { public Guid fmtid; public int pid; }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    static readonly Guid ClsidEnum = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    static readonly Guid IidEnum = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    static readonly Guid IidClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    static readonly Guid IidRender = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    static readonly Guid SubIeeFloat = new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    static readonly Guid SubPcm = new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    static readonly PropertyKey PkeyFriendlyName = new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

    const int ERender = 0, StateActive = 1;
    const int ShareExclusive = 1;
    const int FlagsEventCb = 0x00040000, FlagsNoPersist = 0x00080000;
    const int SilentFlag = 0x2;
    const int HrAligned = unchecked((int)0x88890019); // SDK 头文件确认；引擎 WasapiTypes 里的 0x88890023 是错的
    const int HrUnsupported = unchecked((int)0x88890008);

    [DllImport("ole32")] static extern int CoInitializeEx(IntPtr p, uint coinit);
    [DllImport("ole32")] static extern int CoCreateInstance(ref Guid clsid, IntPtr p, uint ctx, ref Guid iid, out IntPtr obj);
    [DllImport("ole32")] static extern void CoTaskMemFree(IntPtr pv);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr CreateEventW(IntPtr a, bool manual, bool initial, string? name);
    [DllImport("kernel32")] static extern int WaitForSingleObject(IntPtr h, int ms);
    [DllImport("kernel32")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32")] static extern IntPtr AddVectoredExceptionHandler(ulong first, void* handler);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool GetModuleFileNameW(IntPtr mod, char* buf, uint len);
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern bool GetModuleHandleExW(uint flags, IntPtr addr, IntPtr* mod);

    // 崩溃现场记录：RIP 所在模块 + 访问违例地址（纯非托管输出：VEH 内禁止托管分配）
    static int g_vehCount;

    [UnmanagedCallersOnly]
    static nint VectoredHandler(EXCEPTION_POINTERS* info)
    {
        EXCEPTION_RECORD* rec = info->ExceptionRecord;
        if (g_vehCount < 10)
        {
            char* b = stackalloc char[64];
            int k = 0;
            AppendLit(b, &k, "[veh] code=");
            AppendHex(b, &k, (uint)rec->ExceptionCode);
            b[k++] = '';
            b[k++] = '
';
            IntPtr e = GetStdHandle(new IntPtr(-12));
            WriteFile(e, b, (uint)(k * 2), out _, IntPtr.Zero);
        }
        g_vehCount++;
        if ((uint)rec->ExceptionCode != 0xC0000005u) return 0;
        ulong rip = *(ulong*)((byte*)info->ContextRecord + 0xF8); // x64 CONTEXT Rip 固定偏移

        char* buf = stackalloc char[1024];
        int n = 0;
        AppendLit(buf, &n, "[AV] rip=");
        AppendHex(buf, &n, rip);
        AppendLit(buf, &n, " mod=");
        IntPtr mod;
        if (GetModuleHandleExW(0x6, (IntPtr)rip, &mod) && mod != IntPtr.Zero)
        {
            uint fl = 0;
            fixed (char* fm = g_file)
            {
                GetModuleFileNameW(mod, fm, 512);
                while (fm[fl] != 0) { buf[n++] = fm[fl]; fl++; if (n >= 900) break; }
            }
        }
        else AppendLit(buf, &n, "?");
        AppendLit(buf, &n, " acc=");
        AppendHex(buf, &n, rec->ExceptionInformation[1]);
        AppendLit(buf, &n, rec->ExceptionInformation[0] == 0 ? " (READ)" : " (WRITE)");
        buf[n++] = '\r';
        buf[n++] = '\n';

        IntPtr stderr = GetStdHandle(new IntPtr(-12));
        WriteFile(stderr, buf, (uint)(n * 2), out _, IntPtr.Zero);
        return 0; // EXCEPTION_CONTINUE_SEARCH
    }

    [DllImport("kernel32")] static extern IntPtr GetStdHandle(IntPtr handle);
    [DllImport("kernel32")] static extern bool WriteFile(IntPtr h, void* data, uint len, out uint written, IntPtr overlapped);

    // g_mod 已移除：用局部变量取模块句柄
    static char[] g_file = new char[512];

    private static void AppendLit(char* dst, int* n, string lit)
    {
        foreach (char c in lit) dst[(*n)++] = c;
    }

    private static unsafe void AppendHex(char* dst, int* n, ulong v)
    {
        dst[(*n)++] = '0';
        dst[(*n)++] = 'x';
        if (v == 0) { dst[(*n)++] = '0'; return; }
        char* tmp = stackalloc char[16];
        int m = 0;
        while (v != 0) { int d = (int)(v & 0xF); tmp[m++] = (char)(d < 10 ? '0' + d : 'A' + d - 10); v >>= 4; }
        while (m > 0) dst[(*n)++] = tmp[--m];
    }

    [StructLayout(LayoutKind.Sequential)]
    struct EXCEPTION_POINTERS { public EXCEPTION_RECORD* ExceptionRecord; public void* ContextRecord; }

    [StructLayout(LayoutKind.Sequential)]
    struct EXCEPTION_RECORD
    {
        public uint ExceptionCode;
        public uint ExceptionFlags;
        public EXCEPTION_RECORD* ExceptionRecord;
        public void* ExceptionAddress;
        public uint NumberParameters;
        public fixed ulong ExceptionInformation[15];
    }

    // CONTEXT 不建结构体：x64 CONTEXT 布局复杂，Rip 固定偏移 0xF8 直接读

    [DllImport("winmm")] static extern int timeBeginPeriod(uint ms);
    [DllImport("winmm")] static extern int timeEndPeriod(uint ms);

    static WAVEFORMATEXTENSIBLE MakeFormat(int rate, int ch, int kind) => kind switch
    {
        0 => Ext(rate, ch, 32, 32, SubIeeFloat),   // float32
        1 => Ext(rate, ch, 32, 24, SubPcm),        // 24in32
        2 => Ext(rate, ch, 16, 16, SubPcm),        // pcm16
        _ => Ext(rate, ch, 32, 32, SubPcm),        // pcm32
    };

    static WAVEFORMATEXTENSIBLE Ext(int rate, int ch, int bits, int valid, Guid sub)
    {
        var f = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = 0xFFFE, nChannels = (ushort)ch, nSamplesPerSec = (uint)rate,
                wBitsPerSample = (ushort)bits, cbSize = 22,
            },
            wValidBitsPerSample = (ushort)valid, dwChannelMask = ch == 1 ? 0x4u : 0x3u, SubFormat = sub,
        };
        f.Format.nBlockAlign = (ushort)(ch * bits / 8);
        f.Format.nAvgBytesPerSec = f.Format.nSamplesPerSec * f.Format.nBlockAlign;
        return f;
    }

    static int Main(string[] args)
    {
        AddVectoredExceptionHandler(1, (delegate* unmanaged<EXCEPTION_POINTERS*, nint>)&VectoredHandler);
        // 无参数：依次以子进程运行实验——单个实验若在驱动内原生崩溃（AV）不影响其余实验
        // args: [实验ID] [端点索引(可选，覆盖按名称选 Senary)]
        if (args.Length == 0)
        {
            var self = System.Diagnostics.Process.GetCurrentProcess().MainModule!.FileName!;
            var runs = new[] { ("D", ""), ("C", ""), ("A", ""), ("A", "1"), ("A", "2") };
            foreach (var (exp, ep) in runs)
            {
                Console.WriteLine($"========== 实验 {exp} 端点={ep}（独立进程，崩溃可隔离） ==========");
                var psi = new System.Diagnostics.ProcessStartInfo(self, exp + (ep == "" ? "" : " " + ep));
                var p = System.Diagnostics.Process.Start(psi)!;
                p.WaitForExit();
                Console.WriteLine($"[exit code: 0x{p.ExitCode:X8}]");
                Console.WriteLine();
            }
            return 0;
        }

        timeBeginPeriod(1); // 与引擎运行时一致（影响 Sleep 粒度）
        CoInitializeEx(IntPtr.Zero, 0);
        int hr = CoCreateInstance(ref Unsafe.AsRef(in ClsidEnum), IntPtr.Zero, 23, ref Unsafe.AsRef(in IidEnum), out IntPtr en);
        if (hr != 0) { Console.WriteLine($"enumerator hr=0x{hr:X8}"); return 1; }

        var enumVtbl = *(void***)en;
        var enumEndpoints = (delegate* unmanaged[Stdcall]<IntPtr, int, int, void**, int>)enumVtbl[3];
        void* collPtr;
        hr = enumEndpoints(en, ERender, StateActive, &collPtr);
        if (hr != 0) { Console.WriteLine($"EnumAudioEndpoints hr=0x{hr:X8}"); return 1; }
        IntPtr coll = new(collPtr);
        var collVtbl = *(void***)coll;
        var getCount = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)collVtbl[3];
        var getItem = (delegate* unmanaged[Stdcall]<IntPtr, uint, void**, int>)collVtbl[4];
        uint count;
        getCount(coll, &count);
        Console.WriteLine($"活动渲染端点 {count} 个：");

        IntPtr target = IntPtr.Zero;
        string targetName = "";
        uint overrideIdx = args.Length > 1 && uint.TryParse(args[1], out var oi) ? oi : uint.MaxValue;
        for (uint i = 0; i < count; i++)
        {
            void* devPtr;
            if (getItem(coll, i, &devPtr) != 0) continue;
            IntPtr dev = new(devPtr);
            string name = FriendlyName(dev);
            Console.WriteLine($"  [{i}] {name}");
            if (overrideIdx != uint.MaxValue)
            {
                if (i == overrideIdx) { target = dev; targetName = name; }
                continue;
            }
            if (target == IntPtr.Zero && name.Contains("senary", StringComparison.OrdinalIgnoreCase))
            {
                target = dev;
                targetName = name;
            }
        }
        if (target == IntPtr.Zero)
        {
            Console.WriteLine("未找到名称含 Senary 的端点，改用 [0] 作目标");
            void* d0;
            if (getItem(coll, 0, &d0) != 0) return 1;
            target = new IntPtr(d0);
            targetName = FriendlyName(target);
        }
        Console.WriteLine($"目标：{targetName}\n");

        if (args[0] == "A") RunPush(target, "A: PUSH+预填, periodicity=bufferHns（引擎现代码）", periodicityZero: false);
        else if (args[0] == "B") RunPush(target, "B: PUSH+预填, periodicity=0（官方惯例）", periodicityZero: true);
        else if (args[0] == "D") RunPushNoPrefill(target, "D: PUSH 不预填（官方推送样例行为）");
        else RunEvent(target);
        return 0;
    }

    static string FriendlyName(IntPtr dev)
    {
        try
        {
            var devVtbl = *(void***)dev;
            var openStore = (delegate* unmanaged[Stdcall]<IntPtr, int, void**, int>)devVtbl[4];
            void* storePtr;
            if (openStore(dev, 0, &storePtr) != 0) return "<无属性存储>";
            IntPtr store = new(storePtr);
            var stVtbl = *(void***)store;
            var getValue = (delegate* unmanaged[Stdcall]<IntPtr, PropertyKey*, PropVariant*, int>)stVtbl[5];
            var key = PkeyFriendlyName;
            PropVariant pv = default;
            if (getValue(store, &key, &pv) != 0) return "<读取失败>";
            string s = pv.vt == 31 && pv.pointerValue != IntPtr.Zero
                ? Marshal.PtrToStringUni(pv.pointerValue) ?? "?" : $"(vt={pv.vt})";
            CoTaskMemFree(pv.pointerValue);
            Marshal.Release(store);
            return s;
        }
        catch (Exception ex) { return "<异常:" + ex.GetType().Name + ">"; }
    }

    static IntPtr Activate(IntPtr dev)
    {
        var devVtbl = *(void***)dev;
        var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, void*, IntPtr*, int>)devVtbl[3];
        var iid = IidClient;
        IntPtr client;
        return activate(dev, &iid, 23, null, &client) == 0 ? client : IntPtr.Zero;
    }

    /// <summary>独占初始化（含对齐重试）。返回 hr；成功时输出实际缓冲帧数。</summary>
    static int InitExclusive(IntPtr client, IAudioClient api, int rate, int ch, int kind, int latencyMs,
        bool eventMode, bool periodicityZero, out uint bufferFrames)
    {
        bufferFrames = 0;
        long framesWanted = (long)rate * Math.Max(10, latencyMs) / 8000; // 与引擎一致
        long hns = (long)(10000000.0 * framesWanted / rate + 0.5);
        long periodicity = eventMode ? hns : (periodicityZero ? 0 : hns);
        int flags = FlagsNoPersist | (eventMode ? FlagsEventCb : 0);
        // 格式放持久非托管内存（引擎同款）：驱动/音频会话可能留存格式指针到首次 GetBuffer，
        // 栈指针在 InitAny 返回后即失效 → GetBuffer 解引用悬空指针原生 AV
        var fmt = MakeFormat(rate, ch, kind);
        var pFmt = (WAVEFORMATEXTENSIBLE*)NativeMemory.Alloc((nuint)sizeof(WAVEFORMATEXTENSIBLE));
        *pFmt = fmt;
        int hr = api.Initialize(ShareExclusive, flags, hns, periodicity, pFmt);
        if (hr == HrAligned)
        {
            if (api.GetBufferSize(out uint aligned) == 0 && aligned > 0)
            {
                long hns2 = (long)(10000000.0 * aligned / rate + 0.5);
                long per2 = eventMode ? hns2 : (periodicityZero ? 0 : hns2);
                hr = api.Initialize(ShareExclusive, flags, hns2, per2, pFmt);
                if (hr == 0)
                {
                    if (api.GetBufferSize(out bufferFrames) != 0) bufferFrames = aligned;
                    NativeMemory.Free(pFmt);
                    return 0;
                }
            }
        }
        if (hr == 0) api.GetBufferSize(out bufferFrames);
        NativeMemory.Free(pFmt);
        return hr;
    }

    /// <summary>在目标设备上按候选序列初始化独占，失败返回 false。</summary>
    static bool InitAny(IntPtr clientPtr, IAudioClient api, bool eventMode, bool periodicityZero, out uint frames)
    {
        frames = 0;
        foreach (var rate in new[] { 44100, 48000, 96000 })
        {
            foreach (int kind in new[] { 0, 1, 2 })
            {
                int hr = InitExclusive(clientPtr, api, rate, 2, kind, 300, eventMode, periodicityZero, out frames);
                if (hr == 0)
                {
                    Console.WriteLine($"Initialize OK: rate={rate} kind={(kind == 0 ? "float32" : kind == 1 ? "24in32" : "pcm16")} frames={frames}");
                    return true;
                }
                Console.WriteLine($"  Initialize hr=0x{hr:X8} rate={rate} kind={kind}");
                if (hr != HrUnsupported && hr != HrAligned) return false; // 设备级拒绝：换格式无意义
            }
        }
        return false;
    }

    /// <summary>独占 PUSH 运行实验。</summary>
    static void RunPush(IntPtr dev, string label, bool periodicityZero)
    {
        Console.WriteLine($"──────── 实验 {label} ────────");
        IntPtr clientPtr = Activate(dev);
        if (clientPtr == IntPtr.Zero) { Console.WriteLine("Activate 失败\n"); return; }
        var api = new IAudioClient(clientPtr);
        if (!InitAny(clientPtr, api, eventMode: false, periodicityZero, out uint frames))
        {
            Console.WriteLine("独占初始化整体失败——初始化即被拒\n");
            Marshal.Release(clientPtr);
            return;
        }

        IntPtr renderPtr = GetService(clientPtr);
        Console.WriteLine($"GetService(render) hr=0x{(renderPtr == IntPtr.Zero ? -1 : 0):X8} ptr=0x{renderPtr:X}");
        if (renderPtr == IntPtr.Zero) { Console.WriteLine("GetService 失败"); api.Stop(); Marshal.Release(clientPtr); return; }
        var renderVtbl = *(void***)renderPtr;
        var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte**, int>)renderVtbl[3];
        var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)renderVtbl[4];

        byte* pre;
        int pfb = getBuffer(clientPtr, frames, &pre);
        Console.WriteLine($"预填 GetBuffer({frames}) hr=0x{pfb:X8}");
        if (pfb == 0) releaseBuffer(clientPtr, frames, SilentFlag);

        int hr = api.Start();
        Console.WriteLine($"Start hr=0x{hr:X8}，开始 2.5s padding 轨迹追踪（每 ~100ms 采样）：");

        var trace = new List<string>();
        uint padMin = uint.MaxValue, padMax = 0;
        int refills = 0, errors = 0;
        bool oddProbed = false;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastTrace = -100;
        while (sw.ElapsedMilliseconds < 2500)
        {
            Thread.Sleep(2);
            if (api.GetCurrentPadding(out uint pad) != 0) { errors++; break; }
            if (pad < padMin) padMin = pad;
            if (pad > padMax) padMax = pad;
            uint free = frames - pad;
            byte* p1;
            if (free > 0)
            {
                uint want = free;
                if (!oddProbed && free >= 137)
                {
                    want = 137;
                    oddProbed = true;
                    byte* p0;
                    int gb = getBuffer(clientPtr, want, &p0);
                    int rb = gb == 0 ? releaseBuffer(clientPtr, want, SilentFlag) : -1;
                    Console.WriteLine($"  奇数尺寸写入探测: GetBuffer(137) hr=0x{gb:X8} Release hr=0x{rb:X8}");
                    refills++;
                }
                else if (getBuffer(clientPtr, want, &p1) == 0)
                {
                    if (releaseBuffer(clientPtr, want, SilentFlag) == 0) refills++;
                    else errors++;
                }
                else errors++;
            }
            if (sw.ElapsedMilliseconds - lastTrace >= 100)
            {
                lastTrace = sw.ElapsedMilliseconds;
                trace.Add($"{sw.ElapsedMilliseconds,5}ms pad={pad,6} free={frames - pad,6}");
            }
        }

        api.GetCurrentPadding(out uint padEnd);
        api.Stop();
        api.Reset();
        Marshal.Release(clientPtr);

        foreach (var t in trace) Console.WriteLine("  " + t);
        Console.WriteLine($"小结: refill 次数={refills} 错误={errors} pad范围=[{padMin},{padMax}] 初始满值={frames} 结束pad={padEnd}");
        if (padMin == frames)
            Console.WriteLine("  ⚠ padding 全程卡在满值：驱动未推进消费进度 → 推送循环永远 free=0 → 永久静音（机制①确诊）\n");
        else if (refills == 0)
            Console.WriteLine("  ⚠ padding 有移动但从未出现可用空量 → 推送无法写入\n");
        else
            Console.WriteLine($"  padding 移动量 {(padMax - padMin)}/{frames}，推送可写入；量化粒度见上方轨迹\n");
    }

    /// <summary>独占 PUSH 不预填实验：官方推送样例不预填，Start 后按 padding 喂。
    /// 若这样 GetBuffer 不崩而预填版崩，则崩溃点是"Start 前 GetBuffer 满块"。</summary>
    static void RunPushNoPrefill(IntPtr dev, string label)
    {
        Console.WriteLine($"──────── 实验 {label} ────────");
        IntPtr clientPtr = Activate(dev);
        if (clientPtr == IntPtr.Zero) { Console.WriteLine("Activate 失败"); return; }
        var api = new IAudioClient(clientPtr);
        if (!InitAny(clientPtr, api, eventMode: false, periodicityZero: false, out uint frames))
        {
            Console.WriteLine("独占初始化整体失败");
            Marshal.Release(clientPtr);
            return;
        }

        var renderVtbl = *(void***)GetService(clientPtr);
        var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte**, int>)renderVtbl[3];
        var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)renderVtbl[4];

        int hr = api.Start();
        Console.WriteLine($"Start hr=0x{hr:X8}（未预填），按 padding 喂 2.5s：");

        var trace = new List<string>();
        uint padMin = uint.MaxValue, padMax = 0;
        int refills = 0, errors = 0, firstFeedsLogged = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastTrace = -100;
        while (sw.ElapsedMilliseconds < 2500)
        {
            Thread.Sleep(2);
            if (api.GetCurrentPadding(out uint pad) != 0) { errors++; break; }
            if (pad < padMin) padMin = pad;
            if (pad > padMax) padMax = pad;
            uint free = frames - pad;
            if (free > 0)
            {
                byte* p1;
                int gb = getBuffer(clientPtr, free, &p1);
                if (gb == 0)
                {
                    if (releaseBuffer(clientPtr, free, SilentFlag) == 0) refills++;
                    else errors++;
                    if (firstFeedsLogged < 3)
                    {
                        firstFeedsLogged++;
                        Console.WriteLine($"  首次喂写 #{firstFeedsLogged}: GetBuffer({free}) hr=0 良好 @ {sw.ElapsedMilliseconds}ms");
                    }
                }
                else
                {
                    errors++;
                    if (firstFeedsLogged < 3)
                    {
                        firstFeedsLogged++;
                        Console.WriteLine($"  喂写失败: GetBuffer({free}) hr=0x{gb:X8} @ {sw.ElapsedMilliseconds}ms");
                    }
                }
            }
            if (sw.ElapsedMilliseconds - lastTrace >= 100)
            {
                lastTrace = sw.ElapsedMilliseconds;
                trace.Add($"{sw.ElapsedMilliseconds,5}ms pad={pad,6} free={frames - pad,6}");
            }
        }

        api.Stop();
        api.Reset();
        Marshal.Release(clientPtr);

        foreach (var t in trace) Console.WriteLine("  " + t);
        Console.WriteLine($"小结: refill 次数={refills} 错误={errors} pad范围=[{padMin},{padMax}] 满={frames}");
        if (refills > 0) Console.WriteLine("  推送不预填时 GetBuffer 可用 → 崩溃点=Start 前的满块预填");
        else Console.WriteLine("  ⚠ 不预填也无法写入（或无空量出现）");
        Console.WriteLine();
    }

    /// <summary>独占 EVENT 对照实验。</summary>
    static void RunEvent(IntPtr dev)
    {
        Console.WriteLine($"──────── 实验 C: EVENT（整块写，对照） ────────");
        IntPtr clientPtr = Activate(dev);
        if (clientPtr == IntPtr.Zero) { Console.WriteLine("Activate 失败\n"); return; }
        var api = new IAudioClient(clientPtr);
        IntPtr ev = CreateEventW(IntPtr.Zero, false, false, null);
        if (ev == IntPtr.Zero) { Console.WriteLine("CreateEvent 失败\n"); Marshal.Release(clientPtr); return; }

        if (!InitAny(clientPtr, api, eventMode: true, periodicityZero: false, out uint frames))
        {
            Console.WriteLine("独占初始化整体失败\n");
            CloseHandle(ev);
            Marshal.Release(clientPtr);
            return;
        }

        IntPtr renderPtr = GetService(clientPtr);
        if (renderPtr == IntPtr.Zero) { Console.WriteLine("GetService 失败"); api.Stop(); Marshal.Release(clientPtr); CloseHandle(ev); return; }
        var renderVtbl = *(void***)renderPtr;
        var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, byte**, int>)renderVtbl[3];
        var releaseBuffer = (delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)renderVtbl[4];
        int ser = api.SetEventHandle(ev);
        Console.WriteLine($"SetEventHandle hr=0x{ser:X8}");

        byte* pre;
        if (getBuffer(clientPtr, frames, &pre) == 0)
            releaseBuffer(clientPtr, frames, SilentFlag);

        int hr = api.Start();
        Console.WriteLine($"Start hr=0x{hr:X8}，监听事件 2.5s：");

        int events = 0, errors = 0;
        uint padMin = uint.MaxValue, padMax = 0;
        var intervals = new List<long>();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long last = sw.ElapsedMilliseconds;
        while (sw.ElapsedMilliseconds < 2500)
        {
            byte* p2;
            if (WaitForSingleObject(ev, 100) == 0)
            {
                long now = sw.ElapsedMilliseconds;
                intervals.Add(now - last);
                last = now;
                events++;
                api.GetCurrentPadding(out uint pad);
                if (pad < padMin) padMin = pad;
                if (pad > padMax) padMax = pad;
                if (getBuffer(clientPtr, frames, &p2) == 0)
                {
                    if (releaseBuffer(clientPtr, frames, SilentFlag) != 0) errors++;
                }
                else errors++;
            }
        }

        api.Stop();
        api.Reset();
        Marshal.Release(renderPtr);
        Marshal.Release(clientPtr);
        CloseHandle(ev);

        double avg = intervals.Count > 0 ? intervals.Average() : 0;
        Console.WriteLine($"小结: 事件次数={events}/2.5s 平均间隔={avg:F1}ms 错误={errors} pad范围=[{padMin},{padMax}]");
        Console.WriteLine(events > 10 ? "  事件正常触发，事件模式工作\n" : "  ⚠ 事件几乎没有触发\n");
    }

    static IntPtr GetService(IntPtr client)
    {
        var vtbl = *(void***)client;
        var getService = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[14];
        var iid = IidRender;
        IntPtr svc;
        return getService(client, &iid, &svc) == 0 ? svc : IntPtr.Zero;
    }

    /// <summary>最小 IAudioClient 裸虚表包装。</summary>
    private readonly struct IAudioClient
    {
        private readonly IntPtr _self;
        private readonly void** _vtbl;

        public IAudioClient(IntPtr self)
        {
            _self = self;
            _vtbl = *(void***)self;
        }

        public int Initialize(int shareMode, int flags, long bufferDuration, long periodicity, WAVEFORMATEXTENSIBLE* fmt)
            => ((delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)_vtbl[3])(
                _self, shareMode, flags, bufferDuration, periodicity, (WAVEFORMATEX*)fmt, null);

        public int GetBufferSize(out uint frames)
        {
            fixed (uint* p = &frames)
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)_vtbl[4])(_self, p);
        }

        public int GetCurrentPadding(out uint padding)
        {
            fixed (uint* p = &padding)
                return ((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)_vtbl[6])(_self, p);
        }

        public int Start() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[10])(_self);
        public int Stop() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[11])(_self);
        public int Reset() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[12])(_self);

        public int SetEventHandle(IntPtr h)
            => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_vtbl[13])(_self, h);
    }
}
