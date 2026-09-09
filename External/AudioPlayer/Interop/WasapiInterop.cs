using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace AudioPlayer.Interop;

// ─────────────────────────────────────────────────────────────────
// WASAPI 互操作。
// 设备枚举（IMMDeviceEnumerator/Collection/Device/PropertyStore）使用
// GeneratedComInterface（冒烟测试已验证可用）；播放咽喉路径
// （IAudioClient/IAudioRenderClient/ISimpleAudioVolume）使用裸虚表调用，
// 行为完全确定、不依赖生成封送器。
// ─────────────────────────────────────────────────────────────────

[StructLayout(LayoutKind.Sequential, Pack = 2)] // sizeof=18：默认对齐会撑到 20 并错位 Extensible 扩展
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

internal static class WaveFormatTags
{
    public const ushort WAVE_FORMAT_PCM = 0x0001;
    public const ushort WAVE_FORMAT_IEEE_FLOAT = 0x0003;
    public const ushort WAVE_FORMAT_EXTENSIBLE = 0xFFFE;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)] // 40 字节规范布局：validBits@18 mask@20 SubFormat@24
internal struct WAVEFORMATEXTENSIBLE
{
    public WAVEFORMATEX Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;

    public static WAVEFORMATEXTENSIBLE Create(uint sampleRate, ushort channels, ushort bitsPerSample,
        ushort validBits, Guid subFormat)
    {
        var f = new WAVEFORMATEXTENSIBLE
        {
            Format = new WAVEFORMATEX
            {
                wFormatTag = WaveFormatTags.WAVE_FORMAT_EXTENSIBLE,
                nChannels = channels,
                nSamplesPerSec = sampleRate,
                wBitsPerSample = bitsPerSample,
                cbSize = 22,
            },
            wValidBitsPerSample = validBits,
            dwChannelMask = channels switch
            {
                1 => 0x4,          // SPEAKER_FRONT_CENTER
                2 => 0x3,          // SPEAKER_FRONT_LEFT | RIGHT
                _ => 0,
            },
            SubFormat = subFormat,
        };
        f.Format.nBlockAlign = (ushort)(channels * bitsPerSample / 8);
        f.Format.nAvgBytesPerSec = f.Format.nSamplesPerSec * f.Format.nBlockAlign;
        return f;
    }
}

internal static class SubFormats
{
    public static readonly Guid Pcm = new(0x00000001, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
    public static readonly Guid IeeeFloat = new(0x00000003, 0x0000, 0x0010, 0x80, 0x00, 0x00, 0xaa, 0x00, 0x38, 0x9b, 0x71);
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal unsafe partial interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection ppDevices);
    int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr ppEndpoint);
    int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IntPtr ppDevice);
}

[GeneratedComInterface]
[Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
internal unsafe partial interface IMMDeviceCollection
{
    int GetCount(out uint pcDevices);
    int Item(uint nDevice, out IMMDevice ppDevice);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal unsafe partial interface IMMDevice
{
    // 虚表槽位占位：Activate 实际经裸虚表调用（ResolveDevicePtr/ActivateAudioClient），
    // 但声明必须保留以维持 OpenPropertyStore/GetId 的槽位正确。
    int Activate(IntPtr iid, int dwClsCtx, IntPtr pActivationParams, out IntPtr ppInterface);
    int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
    int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
}

[GeneratedComInterface]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
internal unsafe partial interface IPropertyStore
{
    int GetCount(out uint cProps);
    int GetAt(uint iProp, out PropertyKey pkey);
    int GetValue(ref PropertyKey key, out PropVariant value);
}

internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;
}

/// <summary>仅按字节读取的 PROPVARIANT；LPWSTR 场景手工解引用。</summary>
[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct PropVariant
{
    [FieldOffset(0)] public ushort vt;
    [FieldOffset(8)] public IntPtr pointerValue;

    public const ushort VtLpwstr = 31;

    public readonly string? AsString()
    {
        if (vt != VtLpwstr || pointerValue == IntPtr.Zero) return null;
        return Marshal.PtrToStringUni(pointerValue);
    }
}

// ─────────────────────────────────────────────────────────────────
// 裸虚表包装（虚表序与 audioclient.h / audiopolicy.h 一致）
// ─────────────────────────────────────────────────────────────────

/// <summary>IAudioClient（Activate 返回的原生指针），调用方用完 Release。</summary>
internal sealed unsafe class RawAudioClient
{
    private readonly IntPtr _self;
    private readonly void** _vtbl;

    public RawAudioClient(IntPtr self)
    {
        _self = self;
        _vtbl = *(void***)self;
    }

    public int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity,
        WAVEFORMATEX* format) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)_vtbl[3])(
            _self, shareMode, streamFlags, bufferDuration, periodicity, format, null);

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

    public int SetEventHandle(IntPtr handle) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)_vtbl[13])(_self, handle);

    public int GetService(Guid* iid, out IntPtr service)
    {
        fixed (IntPtr* p = &service)
            return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)_vtbl[14])(_self, iid, p);
    }
}

/// <summary>IAudioRenderClient。</summary>
internal sealed unsafe class RawRenderClient
{
    private readonly IntPtr _self;
    private readonly void** _vtbl;

    public RawRenderClient(IntPtr self)
    {
        _self = self;
        _vtbl = *(void***)self;
    }

    public int GetBuffer(uint frames, out byte* buffer)
    {
        byte* local = null;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, byte**, int>)_vtbl[3])(_self, frames, &local);
        buffer = local;
        return hr;
    }

    public int ReleaseBuffer(uint frames, int flags) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)_vtbl[4])(_self, frames, flags);
}

/// <summary>ISimpleAudioVolume（共享模式会话音量，与 bass WasapiShared 语义一致）。</summary>
internal sealed unsafe class RawSessionVolume
{
    private readonly IntPtr _self;
    private readonly void** _vtbl;

    public RawSessionVolume(IntPtr self)
    {
        _self = self;
        _vtbl = *(void***)self;
    }

    // ISimpleAudioVolume 虚表序（audiopolicy.h）：3=SetMasterVolume 4=GetMasterVolume
    // （此前误用槽 4 = GetMasterVolume(float*)，把音量浮点当指针写 → 堆损坏/AV）
    public int SetMasterVolume(float volume) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, float, void*, int>)_vtbl[3])(_self, volume, null);
}

internal static class WasapiTypes
{
    public const int ERender = 0;
    public const int EConsole = 0;
    public const int DeviceStateActive = 0x1;

    public const int ShareModeShared = 0;
    public const int ShareModeExclusive = 1;

    public const int StreamFlagsEventCallback = 0x00040000;
    public const int StreamFlagsNoPersist = 0x00080000;

    public const int BufferFlagsSilent = 0x2;

    // HRESULT
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int EPending = unchecked((int)0x8000000A);
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890023);
    public const int AudclntEUnsupportedFormat = unchecked((int)0x88890008);
    public const int AudclntEDeviceInUse = unchecked((int)0x8889000A);
    public const int AudclntEExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int AudclntEEndpointCreateFailed = unchecked((int)0x8889000F);
    public const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);

    public static readonly Guid IidIAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    public static readonly Guid IidIAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IidISimpleAudioVolume = new("87CE5498-68D6-44E5-9215-6DA47EF883D8");

    public static readonly Guid ClsidMmDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IidIMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");

    public static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
        pid = 14,
    };
}

/// <summary>设备枚举结果缓存（服务器进程生命周期内与 bass 一样按会话缓存）。</summary>
internal sealed class WasapiDeviceList
{
    public sealed record DeviceInfo(int Index, string Id, string FriendlyName, bool IsDefault);

    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public readonly List<DeviceInfo> Devices = [];

    private WasapiDeviceList() { }

    private static T? Wrap<T>(IntPtr ptr) where T : class
        => ptr == IntPtr.Zero ? null : (T)ComWrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.None);

    public static T WrapAlive<T>(IntPtr ptr) where T : class
    {
        var obj = Wrap<T>(ptr);
        return obj ?? throw new InvalidOperationException($"COM 对象创建失败: {typeof(T).Name}");
    }

    /// <summary>枚举活动渲染端点。失败返回空列表。</summary>
    public static WasapiDeviceList Enumerate()
    {
        var list = new WasapiDeviceList();
        int hr = Win32.CoCreateInstance(ref Unsafe.AsRef(in WasapiTypes.ClsidMmDeviceEnumerator), IntPtr.Zero,
            Win32.CLSCTX_ALL, ref Unsafe.AsRef(in WasapiTypes.IidIMMDeviceEnumerator), out IntPtr enumPtr);
        if (hr != 0 || enumPtr == IntPtr.Zero) return list;
        try
        {
            var enumerator = WrapAlive<IMMDeviceEnumerator>(enumPtr);
            enumerator.EnumAudioEndpoints(WasapiTypes.ERender, WasapiTypes.DeviceStateActive, out var collection);
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(WasapiTypes.ERender, WasapiTypes.EConsole, out IntPtr defaultPtr) == 0
                && defaultPtr != IntPtr.Zero)
            {
                var defaultDevice = Wrap<IMMDevice>(defaultPtr);
                if (defaultDevice != null) { try { defaultDevice.GetId(out defaultId); } catch { } }
                Marshal.Release(defaultPtr);
            }

            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var device) != 0) continue;
                try
                {
                    device.GetId(out string id);
                    string name = GetFriendlyName(device) ?? $"WASAPI Device {i}";
                    bool isDefault = defaultId != null && string.Equals(defaultId, id, StringComparison.OrdinalIgnoreCase);
                    list.Devices.Add(new DeviceInfo((int)i, id, name, isDefault));
                }
                catch { }
            }
        }
        catch { }
        finally
        {
            Marshal.Release(enumPtr);
        }
        return list;
    }

    public static string? GetFriendlyName(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return null;
            var key = WasapiTypes.PkeyDeviceFriendlyName;
            store.GetValue(ref key, out var value);
            return value.AsString();
        }
        catch { return null; }
    }

    /// <summary>
    /// 按索引解析端点，返回原生 IMMDevice 指针（调用方负责 Release）。
    /// 索引&lt;0 或越界 → 默认端点。裸虚表调用（枚举器虚表：4=GetDefaultAudioEndpoint，5=GetDevice）。
    /// </summary>
    public static unsafe IntPtr ResolveDevicePtr(int index)
    {
        int hr = Win32.CoCreateInstance(ref Unsafe.AsRef(in WasapiTypes.ClsidMmDeviceEnumerator), IntPtr.Zero,
            Win32.CLSCTX_ALL, ref Unsafe.AsRef(in WasapiTypes.IidIMMDeviceEnumerator), out IntPtr enumPtr);
        if (hr != 0 || enumPtr == IntPtr.Zero) return IntPtr.Zero;
        try
        {
            void** vtbl = *(void***)enumPtr;
            var getDefault = (delegate* unmanaged[Stdcall]<IntPtr, int, int, IntPtr*, int>)vtbl[4];
            var getDevice = (delegate* unmanaged[Stdcall]<IntPtr, char*, IntPtr*, int>)vtbl[5];

            IntPtr dev = IntPtr.Zero;
            if (index >= 0)
            {
                var list = Enumerate();
                var match = list.Devices.FirstOrDefault(d => d.Index == index);
                if (match != null)
                {
                    fixed (char* pId = match.Id)
                    {
                        hr = getDevice(enumPtr, pId, &dev);
                    }
                    if (hr != 0) dev = IntPtr.Zero;
                }
            }
            if (dev == IntPtr.Zero)
            {
                hr = getDefault(enumPtr, WasapiTypes.ERender, WasapiTypes.EConsole, &dev);
                if (hr != 0)
                {
                    Console.WriteLine($"[wasapi] GetDefaultAudioEndpoint(raw) hr=0x{hr:X8}");
                    return IntPtr.Zero;
                }
            }
            return dev;
        }
        catch { return IntPtr.Zero; }
        finally { Marshal.Release(enumPtr); }
    }

    /// <summary>共享模式端点混音格式摘要。</summary>
    public sealed record SharedMixFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

    /// <summary>裸虚表 GetId（IMMDevice 槽 5）取端点 ID 字符串（CoTaskMem 释放）。</summary>
    private static unsafe string? GetDeviceIdRaw(IntPtr device)
    {
        void** vtbl = *(void***)device;
        var getId = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)vtbl[5];
        IntPtr pId = IntPtr.Zero;
        if (getId(device, &pId) != 0 || pId == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(pId); }
        finally { Win32.CoTaskMemFree(pId); }
    }

    /// <summary>
    /// 查询端点共享模式混音格式与设备 ID（GetMixFormat 裸虚表槽 8）。
    /// 本机存在只接受精确混音格式的虚拟声卡（Senary Audio：44.1k 一律 UNSUPPORTED），
    /// 共享模式必须以混音格式初始化，解码器经 swresample 直出混音率/声道。
    /// 调用方按设备 ID 缓存：默认设备索引 -1 在系统默认输出变更后会解析到不同端点。
    /// </summary>
    public static unsafe (SharedMixFormat? Mix, string DeviceId) GetSharedMixFormat(int deviceIndex)
    {
        IntPtr device = ResolveDevicePtr(deviceIndex);
        if (device == IntPtr.Zero) return (null, "");
        try
        {
            string deviceId = GetDeviceIdRaw(device) ?? "";
            IntPtr clientPtr = ActivateAudioClient(device);
            if (clientPtr == IntPtr.Zero) return (null, deviceId);
            try
            {
                void** vtbl = *(void***)clientPtr;
                var getMix = (delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEX**, int>)vtbl[8];
                WAVEFORMATEX* mix = null;
                if (getMix(clientPtr, &mix) != 0 || mix == null) return (null, deviceId);
                try
                {
                    int rate = (int)mix->nSamplesPerSec;
                    int channels = mix->nChannels;
                    int bits = mix->wBitsPerSample;
                    bool isFloat = mix->wFormatTag == 0x0003;
                    if (mix->wFormatTag == 0xFFFE && mix->cbSize >= 22)
                    {
                        var ext = (WAVEFORMATEXTENSIBLE*)mix;
                        isFloat = ext->SubFormat == SubFormats.IeeeFloat;
                    }
                    return (new SharedMixFormat(rate, channels, bits, isFloat), deviceId);
                }
                finally
                {
                    Win32.CoTaskMemFree((IntPtr)mix);
                }
            }
            finally
            {
                Marshal.Release(clientPtr);
            }
        }
        catch { return (null, ""); }
        finally { Marshal.Release(device); }
    }

    /// <summary>激活端点的 IAudioClient（原生指针，调用方负责 Release）。裸虚表 Activate（虚表槽 3）。</summary>
    public static unsafe IntPtr ActivateAudioClient(IntPtr devicePtr)
    {
        if (devicePtr == IntPtr.Zero) return IntPtr.Zero;
        Guid iid = WasapiTypes.IidIAudioClient;
        void** vtbl = *(void***)devicePtr;
        var activate = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, void*, IntPtr*, int>)vtbl[3];
        IntPtr client = IntPtr.Zero;
        int hr = activate(devicePtr, &iid, Win32.CLSCTX_ALL, null, &client);
        if (hr != 0)
        {
            Console.WriteLine($"[wasapi] Activate hr=0x{hr:X8}");
            return IntPtr.Zero;
        }
        return client;
    }
}
