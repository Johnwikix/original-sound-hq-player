using System.Runtime.InteropServices;

namespace AudioPlayer.Interop;

// ─────────────────────────────────────────────────────────────────
// ASIO 互操作：IASIO 手动虚表调用（Windows 上 ASIO 的 long=32 位），
// 驱动经 HKLM\SOFTWARE\ASIO 注册为 COM 组件。虚表序（IUnknown 后 3 槽起）
// 与 Steinberg SDK iasiodrv.h 一致，方法语义对齐 ECHO asio_host.cpp。
// ─────────────────────────────────────────────────────────────────

internal static class AsioConstants
{
    public const int AseOk = 0;
    public const int AseSuccess = 0x3f489015;

    // 采样类型（asio.h ASIOSampleType）
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
    public const int AsioStInt32Lsb16 = 32;
    public const int AsioStInt32Lsb18 = 33;
    public const int AsioStInt32Lsb20 = 34;
    public const int AsioStInt32Lsb24 = 35;
    public const int AsioStDsdInt8Lsb1 = 40;
    public const int AsioStDsdInt8Msb1 = 41;
    public const int AsioStDsdInt8Ner8 = 42;

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

    // ASIOFuture selector
    public const int KAsioSetIoFormat = 12;
    public const int KAsioGetIoFormat = 13;
    public const int KAsioCanDoIoFormat = 14;

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
        // 先按 IASIO IID 直接创建；失败则退回 IUnknown + QI（部分驱动仅支持后者）
        var iid = AsioConstants.IidIAsio;
        int hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /*CLSCTX_INPROC_SERVER*/, ref iid, out IntPtr p);
        if (hr == 0 && p != IntPtr.Zero) return new AsioDriver(p);
        if (p != IntPtr.Zero) Win32.CoTaskMemFree(p); // 失败时不会写出，防御

        var iidUnk = AsioConstants.IidIUnknown;
        hr = Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iidUnk, out IntPtr unk);
        if (hr != 0 || unk == IntPtr.Zero) return null;
        try
        {
            var iidAsio = AsioConstants.IidIAsio;
            hr = QueryInterfaceRaw(unk, iidAsio, out IntPtr asio);
            if (hr == 0 && asio != IntPtr.Zero)
            {
                Marshal.Release(unk);
                return new AsioDriver(asio);
            }
            // QI 失败：少数驱动把 IASIO 放在默认接口上，直接用 IUnknown 虚表（槽位一致）
            return new AsioDriver(unk);
        }
        catch
        {
            Marshal.Release(unk);
            return null;
        }
    }

    private static int QueryInterfaceRaw(IntPtr self, Guid iid, out IntPtr ppv)
    {
        var vtbl = *(void***)self;
        var qi = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)vtbl[0];
        Guid* piid = &iid; // 非托管局部：直接取地址
        IntPtr local = IntPtr.Zero;
        int hr = qi(self, piid, &local);
        ppv = local;
        return hr;
    }

    // ── IASIO 方法（虚表槽位 3..23）──

    public int Init(IntPtr sysHandle) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, int>)_vtbl[3])(_self, (void*)sysHandle);

    public int Start() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[4])(_self);
    public int Stop() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[5])(_self);

    public int GetChannels(out int inputCount, out int outputCount)
    {
        fixed (int* pi = &inputCount, po = &outputCount) // ref/out 参数是可移动变量，需 fixed
            return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int>)_vtbl[6])(_self, pi, po);
    }

    public int GetBufferSize(out int minSize, out int maxSize, out int preferredSize, out int granularity)
    {
        fixed (int* a = &minSize, b = &maxSize, c = &preferredSize, d = &granularity)
            return ((delegate* unmanaged[Stdcall]<IntPtr, int*, int*, int*, int*, int>)_vtbl[8])(_self, a, b, c, d);
    }

    public int CanSampleRate(double rate) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)_vtbl[9])(_self, rate);

    public int GetSampleRate(out double rate)
    {
        fixed (double* p = &rate)
            return ((delegate* unmanaged[Stdcall]<IntPtr, double*, int>)_vtbl[10])(_self, p);
    }

    public int SetSampleRate(double rate) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, double, int>)_vtbl[11])(_self, rate);

    public int GetChannelInfo(ref AsioChannelInfo info)
    {
        fixed (AsioChannelInfo* p = &info)
            return ((delegate* unmanaged[Stdcall]<IntPtr, AsioChannelInfo*, int>)_vtbl[15])(_self, p);
    }

    public int CreateBuffers(AsioBufferInfo* bufferInfos, int numChannels, int bufferSize, AsioCallbacks* callbacks)
        => ((delegate* unmanaged[Stdcall]<IntPtr, AsioBufferInfo*, int, int, AsioCallbacks*, int>)_vtbl[16])(
            _self, bufferInfos, numChannels, bufferSize, callbacks);

    public int DisposeBuffers() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[17])(_self);

    public int Future(int selector, void* opt) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, void*, int>)_vtbl[19])(_self, selector, opt);

    public int OutputReady() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)_vtbl[20])(_self);

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
