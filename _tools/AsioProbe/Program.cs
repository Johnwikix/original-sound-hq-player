// ASIO 驱动加载探针：FiiO(Thesycon, ThreadingModel=Apartment) 加载组合试验。
// A: STA + CoCreateInstance(clsid-as-riid)
// B: STA + CoCreateInstance(IUnknown) 直接当 IASIO 虚表调用（同套间直调）
// C: LoadLibrary + DllGetClassObject + IClassFactory::CreateInstance（bassasio 等效）
using System.Runtime.InteropServices;
using System.Collections.Concurrent;

internal static unsafe class Program
{
    [StructLayout(LayoutKind.Sequential)]
    struct WNDCLASSW
    {
        public uint style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y; }

    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] static extern ushort RegisterClassW(ref WNDCLASSW wc);
    [DllImport("user32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32")] static extern IntPtr DefWindowProcW(IntPtr h, uint m, nuint w, nint l);
    [DllImport("user32")] static extern void PostQuitMessage(int c);
    [DllImport("user32")] static extern bool GetMessageW(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32")] static extern bool DispatchMessageW(ref MSG msg);
    [DllImport("user32", SetLastError = true)] static extern bool PostMessageW(IntPtr h, uint m, nuint w, nint l);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr GetModuleHandleW(IntPtr n);
    [DllImport("ole32")] static extern int CoInitializeEx(IntPtr r, uint co);
    [DllImport("ole32")] static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint ctx, ref Guid riid, out IntPtr ppv);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
    [DllImport("kernel32", SetLastError = true)] static extern IntPtr GetProcAddress(IntPtr mod, string name);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
    static nint WndProc(IntPtr h, uint m, nuint w, nint l)
    {
        if (m == 2) PostQuitMessage(0);
        return DefWindowProcW(h, m, w, l);
    }

    const uint WmApp = 0x0401;
    static IntPtr _hwnd;
    static readonly ConcurrentQueue<Action> _work = new();
    static string _mode = "B";
    static string? _dllPath;
    static readonly Guid Clsid = new("{6B3BA606-8664-4426-8994-0F1E6FE6199F}");
    static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");

    static int Main(string[] args)
    {
        _mode = args.Length > 0 ? args[0] : "B";
        _dllPath = args.Length > 1 ? args[1] : null;

        var sta = new Thread(() =>
        {
            CoInitializeEx(IntPtr.Zero, 2 /*STA*/);
            var wc = new WNDCLASSW
            {
                lpfnWndProc = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, nint>)&WndProc,
                hInstance = GetModuleHandleW(IntPtr.Zero),
                lpszClassName = "AsioProbeWindow",
            };
            RegisterClassW(ref wc);
            _hwnd = CreateWindowExW(0, "AsioProbeWindow", "probe", 0x00CF0000, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero); // WS_OVERLAPPEDWINDOW
            Console.WriteLine($"[probe] window 0x{_hwnd:X}");
            while (GetMessageW(out var msg, IntPtr.Zero, 0, 0))
            {
                if (msg.message == WmApp) { while (_work.TryDequeue(out var w)) w(); }
                DispatchMessageW(ref msg);
            }
        });
        sta.Start();
        while (_hwnd == IntPtr.Zero) Thread.Sleep(10);

        RunOnSta(RunBody);

        Console.WriteLine("[probe] done");
        PostMessageW(_hwnd, 0x0010, 0, 0);
        Thread.Sleep(300);
        return 0;
    }

    static void RunBody()
    {
        Console.WriteLine($"[probe {_mode}] begin on STA");
        Guid clsid = Clsid;
        IntPtr drv = IntPtr.Zero;
        int hr = -1;

        if (_mode is "A" or "B" or "B0" or "B2")
        {
            Guid riid = _mode == "A" ? clsid : IidUnknown;
            hr = CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref riid, out drv);
            Console.WriteLine($"[probe {_mode}] CoCreateInstance hr=0x{hr:X8} ptr=0x{drv:X}");
        }
        else // C
        {
            if (_dllPath == null) { Console.WriteLine("[probe C] 需要传 dll 路径"); return; }
            var mod = LoadLibraryW(_dllPath);
            Console.WriteLine($"[probe C] LoadLibrary 0x{mod:X}");
            var dgco = GetProcAddress(mod, "DllGetClassObject");
            var f = (delegate* unmanaged[Stdcall]<Guid*, Guid*, IntPtr*, int>)dgco;
            IntPtr factory = IntPtr.Zero;
            Guid clsidL = clsid, factoryL = IidClassFactory;
            int hr2 = f(&clsidL, &factoryL, &factory);
            Console.WriteLine($"[probe C] DllGetClassObject hr=0x{hr2:X8} factory=0x{factory:X}");
            if (hr2 == 0 && factory != IntPtr.Zero)
            {
                var vt = *(void***)factory;
                var create = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)vt[3];
                Guid wanted = clsid;
                hr = create(factory, IntPtr.Zero, &wanted, &drv);
                Console.WriteLine($"[probe C] CreateInstance(clsid-riid) hr=0x{hr:X8} ptr=0x{drv:X}");
                if (hr != 0)
                {
                    Guid unk = IidUnknown;
                    hr = create(factory, IntPtr.Zero, &unk, &drv);
                    Console.WriteLine($"[probe C] CreateInstance(IUnknown) hr=0x{hr:X8} ptr=0x{drv:X}");
                }
            }
        }

        if (hr != 0 || drv == IntPtr.Zero) { Console.WriteLine("[probe] 未取得对象"); return; }


        var vtbl = *(void***)drv;
        var getVersion = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[5];
        Console.WriteLine($"[probe] getDriverVersion -> {getVersion(drv)}");

        var init = (delegate* unmanaged[Stdcall]<IntPtr, void*, int>)vtbl[3];
        void* sysRef = _mode == "B0" ? null : (void*)_hwnd;
        int ir = init(drv, sysRef);
        Console.WriteLine($"[probe] init(sysRef={(_mode == "B0" ? "NULL" : "hwnd")}) -> {ir} (1=成功)");
        if (ir == 0 && _mode == "B2")
        {
            ir = init(drv, sysRef);
            Console.WriteLine($"[probe] init-2nd -> {ir}");
        }

        // 无论成败都读错误消息（槽 6 getErrorMessage(char*)）
        byte* errBuf = stackalloc byte[256];
        for (int i = 0; i < 256; i++) errBuf[i] = 0;
        var getErr = (delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)vtbl[6];
        getErr(drv, errBuf);
        int len = 0; while (len < 255 && errBuf[len] != 0) len++;
        if (len > 0) Console.WriteLine("[probe] driver error: " + System.Text.Encoding.ASCII.GetString(errBuf, len));

        {
            var getChannels = (delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)vtbl[9];
            int inCh = 0, outCh = 0;
            int gc = getChannels(drv, &inCh, &outCh);
            Console.WriteLine($"[probe] getChannels hr={gc} in={inCh} out={outCh}");
        }
    }

    static void RunOnSta(Action body)
    {
        var done = new ManualResetEventSlim(false);
        _work.Enqueue(() => { try { body(); } catch (Exception ex) { Console.WriteLine("[probe] ex: " + ex.Message); } finally { done.Set(); } });
        PostMessageW(_hwnd, WmApp, 0, 0);
        done.Wait(15000);
    }
}
