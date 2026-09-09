using System.Runtime.InteropServices;

namespace AudioPlayer.Interop;

// ─────────────────────────────────────────────────────────────────
// ASIO 互操作：IASIO 手动虚表调用（Windows 上 ASIO 的 long=32 位），
// 驱动经 HKLM\SOFTWARE\ASIO 注册为 COM 组件。虚表序（IUnknown 后 3..23）：
// 3=init 4=getDriverName 5=getDriverVersion 6=getErrorMessage 7=start 8=stop
// 9=getChannels 11=getBufferSize 12=canSampleRate 13=getSampleRate 14=setSampleRate
// 18=getChannelInfo 19=createBuffers 20=disposeBuffers 22=future 23=outputReady
// ═══ 曾因漏计 getDriverName/Version/ErrorMessage 三槽整体偏移 3 导致闪退 ═══
// ─────────────────────────────────────────────────────────────────

internal static class AsioConstants
{
    public const int AseOk = 0;
    public const int AseSuccess = 0x3f489015;

    // 采样类型（asio.h ASIOSampleType，逐值对照过 ECHO 所带官方 SDK 头）
    public const int AsioStInt16Msb = 0;
    public const int AsioStInt24Msb = 1;
    public const int AsioStInt32Msb = 2;
    public const int AsioStFloat32Msb = 3;
    public const int AsioStFloat64Msb = 4;
    public const int AsioStInt32Msb16 = 8;
    public const int AsioStInt32Msb18 = 9;
    public const int AsioStInt32Msb20 = 10;
    public const int AsioStInt32Msb24 = 11;
    public const int AsioStInt16Lsb = 16;
    public const int AsioStInt24Lsb = 17;
    public const int AsioStInt32Lsb = 18;
    public const int AsioStFloat32Lsb = 19;
    public const int AsioStFloat64Lsb = 20;
    public const int AsioStInt32Lsb16 = 24; // 32 位容器 16 位对齐（曾误写 32，与 DSD 类型值冲突）
    public const int AsioStInt32Lsb18 = 25;
    public const int AsioStInt32Lsb20 = 26;
    public const int AsioStInt32Lsb24 = 27;
    public const int AsioStDsdInt8Lsb1 = 32; // DSD：首采样在最低位
    public const int AsioStDsdInt8Msb1 = 33; // DSD：首采样在最高位
    public const int AsioStDsdInt8Ner8 = 40; // DSD：每字节 1 个采样

    // asioMessage selector
    public const int KAsioSelectorSupported = 1;
    public const int KAsioEngineVersion = 2;
    public const int KAsioResetRequest = 3;
    public const int KAsioBufferSizeChange = 4;
    public const int KAsioResyncRequest = 5;
    public const int KAsioLatenciesChanged = 6;
    public const int KAsioSupportsTimeInfo = 7;
    public const int KAsioSupportsTimeCode = 8;
    public const int KAsioSupportsInputMonitor = 10;

    // ASIOFuture selector（asio.h：DSD 扩展是魔数，不是小整数——曾误写 12/13/14 导致
    // 支持 native DSD 的驱动被误判为不支持而回退）
    public const int KAsioSetIoFormat = 0x23111961;
    public const int KAsioGetIoFormat = 0x23111983;
    public const int KAsioCanDoIoFormat = 0x23112004;

    public const int KAsioPcmFormat = 0;
    public const int KAsioDsdFormat = 1;

    public static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    public static readonly Guid IidIAsio = new("232E5E5C-792A-4DCA-993B-4B4D4A1011D0");
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsioBufferInfo
{
    public int IsInput;
    public int ChannelNum;
    public IntPtr Buffer0;
    public IntPtr Buffer1;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AsioChannelInfo
{
    public int Channel;
    public int IsInput;
    public int IsActive;      // SDK 还有这两个字段（曾漏掉导致 Type 读到 channelGroup，
    public int ChannelGroup;  // 输出通道组=1 被当成 Int24MSB → 按 3 字节写 4 字节缓冲 → 全噪声）
    public int Type;
    public fixed byte Name[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct AsioCallbacks
{
    public IntPtr BufferSwitch;
    public IntPtr SampleRateDidChange;
    public IntPtr AsioMessage;
    public IntPtr BufferSwitchTimeInfo;
}

/// <summary>IASIO 驱动包装：手动虚表函数指针调用。</summary>
internal static class AsioNativeLoad
{
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr LoadLibraryW(string path);

    [DllImport("kernel32", SetLastError = true)]
    public static extern IntPtr GetProcAddress(IntPtr mod, string name);

    public static readonly Guid IidClassFactory = new("00000001-0000-0000-C000-000000000046");
}

internal sealed unsafe class AsioDriver : IDisposable
{
    private IntPtr _self;
    private void** _vtbl;
    private bool _disposed;

    private AsioDriver(IntPtr self)
    {
        _self = self;
        _vtbl = *(void***)self;
    }

    public static AsioDriver? Create(Guid clsid)
    {
        // ── 首选：LoadLibrary + DllGetClassObject 直连工厂（bassasio 等效路径）──
        // FiiO(Thesycon) 等大量 ASIO 驱动注册为 ThreadingModel=Apartment：经 CoCreateInstance
        // 激活会拿到代理/错误套间对象（实测 getDriverVersion=6，init 即崩/栈溢出），而直连
        // 工厂 + CreateInstance(clsid 当 riid) 拿到真对象（实测 version=1354，init 成功）。
        var dll = Win32.ReadInprocServer32(clsid);
        if (dll != null && System.IO.File.Exists(dll))
        {
            var mod = AsioNativeLoad.LoadLibraryW(dll);
            if (mod != IntPtr.Zero)
            {
                var dgcoPtr = AsioNativeLoad.GetProcAddress(mod, "DllGetClassObject");
                if (dgcoPtr != IntPtr.Zero)
                {
                    var dgco = (delegate* unmanaged[Stdcall]<Guid*, Guid*, IntPtr*, int>)dgcoPtr;
                    Guid clsidL = clsid, factoryL = AsioNativeLoad.IidClassFactory;
                    IntPtr factory = IntPtr.Zero;
                    if (dgco(&clsidL, &factoryL, &factory) == 0 && factory != IntPtr.Zero)
                    {
                        var fvt = *(void***)factory;
                        var createInstance = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)fvt[3];
                        Guid wanted = clsid; // CLSID 充当 riid：驱动类对象默认接口即 IASIO
                        IntPtr drv = IntPtr.Zero;
                        if (createInstance(factory, IntPtr.Zero, &wanted, &drv) == 0 && drv != IntPtr.Zero)
                        {
                            Marshal.Release(factory);
                            Console.WriteLine($"[asio] loaded via DllGetClassObject: {Path.GetFileName(dll)}");
                            return new AsioDriver(drv);
                        }
                        Marshal.Release(factory);
                    }
                }
            }
            Console.WriteLine("[asio] DllGetClassObject path failed, falling back to CoCreateInstance");
        }

        // ── 回退：CoCreateInstance（IASIO 无官方 IID；CLSID 当 riid → IUnknown 直用）──
        var riid = clsid;
        int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*INPROC*/, ref riid, out IntPtr p);
        if (hr == 0 && p != IntPtr.Zero) return new AsioDriver(p);
        Console.WriteLine($"[asio] Create(clsid-as-riid) hr=0x{hr:X8}，回退 IUnknown");

        var iidUnk = AsioConstants.IidIUnknown;
        hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iidUnk, out IntPtr unk);
        if (hr == 0 && unk != IntPtr.Zero) return new AsioDriver(unk);
        Console.WriteLine($"[asio] Create(IUnknown) hr=0x{hr:X8}");
        return null;
    }

    // ── IASIO 方法（虚表槽位 3..23）──

    public int Init(IntPtr sysHandle) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)_vtbl[3])(_self, (void*)sysHandle);

    public int Start() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[7])(_self);
    public int Stop() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[8])(_self);

    public int GetChannels(out int inputCount, out int outputCount)
    {
        fixed (int* pi = &inputCount, po = &outputCount) // ref/out 参数是可移动变量，需 fixed
            return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)_vtbl[9])(_self, pi, po);
    }

    public int GetBufferSize(out int minSize, out int maxSize, out int preferredSize, out int granularity)
    {
        fixed (int* a = &minSize, b = &maxSize, c = &preferredSize, d = &granularity)
            return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, int>)_vtbl[11])(_self, a, b, c, d);
    }

    public int CanSampleRate(double rate) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)_vtbl[12])(_self, rate);

    public int GetSampleRate(out double rate)
    {
        fixed (double* p = &rate)
            return ((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)_vtbl[13])(_self, p);
    }

    public int SetSampleRate(double rate) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)_vtbl[14])(_self, rate);

    public int GetChannelInfo(ref AsioChannelInfo info)
    {
        fixed (AsioChannelInfo* p = &info)
            return ((delegate* unmanaged[Stdcall]<IntPtr, AsioChannelInfo*, int>)_vtbl[18])(_self, p);
    }

    public int CreateBuffers(AsioBufferInfo* bufferInfos, int numChannels, int bufferSize, AsioCallbacks* callbacks)
        => ((delegate* unmanaged[Stdcall]<IntPtr, AsioBufferInfo*, int, int, AsioCallbacks*, int>)_vtbl[19])(
            _self, bufferInfos, numChannels, bufferSize, callbacks);

    public int DisposeBuffers() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[20])(_self);

    public int Future(int selector, void* opt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)_vtbl[22])(_self, selector, opt);

    public int OutputReady() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[23])(_self);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_self != IntPtr.Zero)
        {
            Marshal.Release(_self);
            _self = IntPtr.Zero;
        }
    }
}
