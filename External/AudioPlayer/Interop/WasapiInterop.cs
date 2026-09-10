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
    // 共享模式"直传 PCM"：音频引擎按需插入采样率转换器与声道矩阵，把任意 PCM 格式转成端点混音格式
    //（DirectSound / miniaudio 共享输出在底层就是靠这两个标志把格式交给系统的）
    public const int StreamFlagsAutoConvertPcm = unchecked((int)0x80000000);
    public const int StreamFlagsSrcDefaultQuality = 0x08000000;

    public const int BufferFlagsSilent = 0x2;

    // ERole（默认设备角色）
    public const int RoleConsole = 0;
    public const int RoleMultimedia = 1;
    public const int RoleCommunications = 2;

    // HRESULT
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int EPending = unchecked((int)0x8000000A);
    // SDK audioclient.h：AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED = 0x88890019。旧值 0x88890023
    // 实为 OUT_OF_OFFLOAD_RESOURCES，导致对齐重试从不触发——Senary 事件独占实测被 0x19 拒绝
    public const int AudclntEBufferSizeNotAligned = unchecked((int)0x88890019);
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
    public static IntPtr ResolveDevicePtr(int index) => ResolveDevicePtr(index, out _);

    /// <summary>同上；isDefault = 最终落到了系统默认端点（显式索引解析失败也算跟随默认）。</summary>
    public static unsafe IntPtr ResolveDevicePtr(int index, out bool isDefault)
    {
        isDefault = true;
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
                    else isDefault = false;
                }
            }
            if (dev == IntPtr.Zero)
            {
                isDefault = true;
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
    public static unsafe string? GetDeviceIdRaw(IntPtr device)
    {
        if (device == IntPtr.Zero) return null;
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

/// <summary>端点事件种类（引擎侧只关心"当前输出所在端点是否还能用"）。</summary>
internal enum EndpointEventKind : byte
{
    /// <summary>设备状态变化（newState ≠ ACTIVE 才投递：禁用/拔出/不存在）。</summary>
    StateChanged,
    /// <summary>设备被移除。</summary>
    Removed,
    /// <summary>端点格式属性（PKEY_AudioEngine_DeviceFormat）变化：系统"输出音频格式"被改。</summary>
    FormatChanged,
}

/// <summary>
/// IMMNotificationClient 裸虚表实现（ECHO wasapi_shared DeviceWatcher 对等；AOT 安全，不依赖生成封送器）。
/// "DirectSound 全自动"的设备侧支撑：默认渲染设备变更 / 当前端点被禁用或拔出 / 系统改输出格式
/// 时由 MMDevice 主动通知，引擎据此静默换输出——不再靠渲染线程超时探测（最坏 2s 静音）。
/// 回调发生在 MMDevice 的 RPC 线程：这里只记录事件，重建一律在引擎看门狗线程做
/// （回调内持引擎锁做驱动调用会与 Initialize/Stop 死锁）。对象为进程生命周期静态单例，Release 不释放。
/// </summary>
internal static unsafe class EndpointNotifications
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int EPointer = unchecked((int)0x80004003);
    private const int DeviceStateActive = 0x1;

    private static readonly Guid IidIUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidIMMNotificationClient = new("7991EEC9-7E89-4D85-8390-6C703CEC60C0");
    /// <summary>PKEY_AudioEngine_DeviceFormat（fmtid，pid=0）：端点混音格式。</summary>
    private static readonly Guid FmtidAudioEngineDeviceFormat = new("F19F064D-082C-4E27-BC73-6882A1BB8E4C");

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public void** Vtbl;
        public int RefCount;
    }

    private static readonly object Gate = new();
    private static IntPtr _enumerator; // 注册期间必须持有（Unregister 需同一实例）
    private static Instance* _instance;
    private static void** _vtbl;

    // 事件投递：默认设备 ID 只保留最新（连续切换只需落到最终设备），其余事件排队由看门狗一次性消费
    private static string? _defaultRenderId;
    private static int _defaultChanged;
    private static readonly System.Collections.Concurrent.ConcurrentQueue<(EndpointEventKind Kind, string DeviceId)> Pending = new();

    // 函数指针须在静态字段中固定：驱动/系统长期持有虚表地址
    private static readonly delegate* unmanaged[Stdcall]<void*, Guid*, void**, int> QueryInterfacePtr = &QueryInterface;
    private static readonly delegate* unmanaged[Stdcall]<void*, uint> AddRefPtr = &AddRef;
    private static readonly delegate* unmanaged[Stdcall]<void*, uint> ReleasePtr = &Release;
    private static readonly delegate* unmanaged[Stdcall]<void*, char*, uint, int> OnDeviceStateChangedPtr = &OnDeviceStateChanged;
    private static readonly delegate* unmanaged[Stdcall]<void*, char*, int> OnDeviceAddedPtr = &OnDeviceAdded;
    private static readonly delegate* unmanaged[Stdcall]<void*, char*, int> OnDeviceRemovedPtr = &OnDeviceRemoved;
    private static readonly delegate* unmanaged[Stdcall]<void*, int, int, char*, int> OnDefaultDeviceChangedPtr = &OnDefaultDeviceChanged;
    private static readonly delegate* unmanaged[Stdcall]<void*, char*, PropertyKey*, int> OnPropertyValueChangedPtr = &OnPropertyValueChanged;

    public static bool IsActive => _enumerator != IntPtr.Zero;

    /// <summary>向系统注册端点通知。幂等；失败返回 false（引擎退化为仅靠渲染失效探测）。</summary>
    public static bool Start()
    {
        lock (Gate)
        {
            if (_enumerator != IntPtr.Zero) return true;
            Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED); // 已初始化返回 S_FALSE/CHANGED_MODE，均无害
            int hr = Win32.CoCreateInstance(ref Unsafe.AsRef(in WasapiTypes.ClsidMmDeviceEnumerator), IntPtr.Zero,
                Win32.CLSCTX_ALL, ref Unsafe.AsRef(in WasapiTypes.IidIMMDeviceEnumerator), out IntPtr enumPtr);
            if (hr != 0 || enumPtr == IntPtr.Zero)
            {
                Console.WriteLine($"[wasapi] notification enumerator hr=0x{hr:X8}");
                return false;
            }

            _vtbl = (void**)NativeMemory.Alloc((nuint)(8 * sizeof(void*)));
            _vtbl[0] = (void*)QueryInterfacePtr;
            _vtbl[1] = (void*)AddRefPtr;
            _vtbl[2] = (void*)ReleasePtr;
            _vtbl[3] = (void*)OnDeviceStateChangedPtr;
            _vtbl[4] = (void*)OnDeviceAddedPtr;
            _vtbl[5] = (void*)OnDeviceRemovedPtr;
            _vtbl[6] = (void*)OnDefaultDeviceChangedPtr;
            _vtbl[7] = (void*)OnPropertyValueChangedPtr;
            _instance = (Instance*)NativeMemory.Alloc((nuint)sizeof(Instance));
            _instance->Vtbl = _vtbl;
            _instance->RefCount = 1;

            // IMMDeviceEnumerator 虚表：6=RegisterEndpointNotificationCallback 7=Unregister
            var register = (delegate* unmanaged[Stdcall]<IntPtr, void*, int>)(*(void***)enumPtr)[6];
            hr = register(enumPtr, _instance);
            if (hr != 0)
            {
                Console.WriteLine($"[wasapi] RegisterEndpointNotificationCallback hr=0x{hr:X8}");
                Marshal.Release(enumPtr);
                NativeMemory.Free(_instance); _instance = null;
                NativeMemory.Free(_vtbl); _vtbl = null;
                return false;
            }
            _enumerator = enumPtr;
            Console.WriteLine("[wasapi] endpoint notifications registered");
            return true;
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_enumerator == IntPtr.Zero) return;
            try
            {
                var unregister = (delegate* unmanaged[Stdcall]<IntPtr, void*, int>)(*(void***)_enumerator)[7];
                unregister(_enumerator, _instance);
            }
            catch { }
            Marshal.Release(_enumerator);
            _enumerator = IntPtr.Zero;
            // 实例/虚表有意不释放：系统侧可能仍有在途回调引用（进程退出回收）
        }
    }

    /// <summary>
    /// 取走自上次以来的全部事件。defaultRenderId = 最新默认渲染端点（未变更为 null；变更为"无设备"时为空串）。
    /// </summary>
    public static bool TryDrain(out string? defaultRenderId, out List<(EndpointEventKind Kind, string DeviceId)> events)
    {
        defaultRenderId = null;
        if (Interlocked.Exchange(ref _defaultChanged, 0) != 0)
            defaultRenderId = Volatile.Read(ref _defaultRenderId) ?? "";
        events = [];
        while (Pending.TryDequeue(out var e)) events.Add(e);
        return defaultRenderId != null || events.Count > 0;
    }

    // ─────────────── IUnknown ───────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int QueryInterface(void* self, Guid* riid, void** ppv)
    {
        if (ppv == null) return EPointer;
        if (riid != null && (*riid == IidIUnknown || *riid == IidIMMNotificationClient))
        {
            *ppv = self;
            Interlocked.Increment(ref ((Instance*)self)->RefCount);
            return 0;
        }
        *ppv = null;
        return ENoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint AddRef(void* self) => (uint)Interlocked.Increment(ref ((Instance*)self)->RefCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static uint Release(void* self)
    {
        int n = Interlocked.Decrement(ref ((Instance*)self)->RefCount);
        return (uint)Math.Max(0, n); // 静态单例：归零也不释放
    }

    // ─────────────── IMMNotificationClient ───────────────

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceStateChanged(void* self, char* deviceId, uint newState)
    {
        try
        {
            if (newState != DeviceStateActive && deviceId != null)
                Pending.Enqueue((EndpointEventKind.StateChanged, new string(deviceId)));
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceAdded(void* self, char* deviceId) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDeviceRemoved(void* self, char* deviceId)
    {
        try
        {
            if (deviceId != null) Pending.Enqueue((EndpointEventKind.Removed, new string(deviceId)));
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnDefaultDeviceChanged(void* self, int flow, int role, char* defaultDeviceId)
    {
        try
        {
            // 只关心渲染方向的 console/multimedia 默认设备（通话设备变更与播放无关）
            if (flow == WasapiTypes.ERender && role != WasapiTypes.RoleCommunications)
            {
                Volatile.Write(ref _defaultRenderId, defaultDeviceId == null ? "" : new string(defaultDeviceId));
                Interlocked.Exchange(ref _defaultChanged, 1);
            }
        }
        catch { }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    private static int OnPropertyValueChanged(void* self, char* deviceId, PropertyKey* key)
    {
        try
        {
            // PROPERTYKEY 20 字节按值传递：x64/ARM64 ABI 均以指针传入调用方副本
            if (deviceId != null && key != null && key->pid == 0 && key->fmtid == FmtidAudioEngineDeviceFormat)
                Pending.Enqueue((EndpointEventKind.FormatChanged, new string(deviceId)));
        }
        catch { }
        return 0;
    }
}
