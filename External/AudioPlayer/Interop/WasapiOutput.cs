using System.Runtime.InteropServices;
using AudioPlayer.Playback;

namespace AudioPlayer.Interop;

/// <summary>
/// WASAPI 输出（ECHO wasapi_exclusive/wasapi_shared 移植）：
/// - 共享：源格式 float32 直接 Initialize（引擎自动重采样），事件驱动渲染，会话音量；
/// - 独占 Push/Event：候选格式协商（PCM: float32→24in32→16→32；DoP: 24packed→24in32→32）、
///   缓冲对齐重试（AUDCLNT_E_BUFFER_SIZE_NOT_ALIGNED）、MMCSS Pro Audio 渲染线程；
/// - DoP：uint32 采样位精确透传（24packed 取低 24 位、24in32/32 左移 8 位）。
/// Initialize 走 3 秒超时包装（驱动死锁时放弃而非挂死整个进程，ECHO future-graveyard 语义）。
/// </summary>
internal sealed unsafe class WasapiOutput : IAudioOutput, IDisposable
{
    private readonly bool _exclusive;
    private readonly bool _pushMode; // 独占轮询模式（非事件驱动）

    private RawAudioClient? _client;
    private IntPtr _clientPtr; // 原生引用（我方持有，Dispose 时 Release）
    private IntPtr _devicePtr;
    private RawRenderClient? _render;
    private IntPtr _renderPtr;
    private RawSessionVolume? _sessionVolume;
    private IntPtr _sessionVolumePtr;
    private IntPtr _renderEvent;
    private IntPtr _stopEvent;
    private Thread? _thread;
    private int _failed;
    private volatile bool _pausedFlag; // 渲染线程暂停门控：暂停期间不得消费环形缓冲
    private volatile bool _initTimedOut; // Initialize 超时：worker 仍持有 client，Dispose 不得再触碰（墓园语义）

    private uint _bufferFrames;
    private uint _channels;
    private int _endpointKind; // FormatKind
    private IRenderSource _source = null!;
    private float[] _pcmScratch = [];
    private uint[] _dopScratch = [];

    public bool IsFailed => Volatile.Read(ref _failed) != 0;

    public int LatencyMs { get; private set; }

    public WasapiOutput(bool exclusive, bool pushMode)
    {
        _exclusive = exclusive;
        _pushMode = pushMode;
    }

    public bool Start(int deviceIndex, int latencyMs, IRenderSource source)
    {
        _source = source;
        Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        try
        {
            _devicePtr = WasapiDeviceList.ResolveDevicePtr(deviceIndex);
            if (_devicePtr == IntPtr.Zero) { Console.WriteLine("[wasapi] ResolveDevice failed"); return false; }
            _clientPtr = WasapiDeviceList.ActivateAudioClient(_devicePtr);
            if (_clientPtr == IntPtr.Zero) return false;
            _client = new RawAudioClient(_clientPtr);

            if (_exclusive)
            {
                int bufferFramesWanted = (int)((long)source.SampleRate * Math.Max(10, latencyMs) / 8000);
                if (!InitializeExclusive(source, bufferFramesWanted))
                {
                    Console.WriteLine($"[wasapi] exclusive init failed mode={(_pushMode ? "push" : "event")}");
                    return false;
                }
            }
            else
            {
                int bufferFramesWanted = (int)((long)source.SampleRate * Math.Max(50, latencyMs) / 1000);
                if (!InitializeShared(source, bufferFramesWanted)) return false;
            }

            Guid iidRender = WasapiTypes.IidIAudioRenderClient;
            int gsr = _client.GetService(&iidRender, out IntPtr renderPtr);
            if (gsr != 0) { Console.WriteLine($"[wasapi] GetService(render) hr=0x{gsr:X8}"); return false; }
            _renderPtr = renderPtr;
            _render = new RawRenderClient(renderPtr);

            // 共享模式会话音量（与 bass WasapiShared 语义一致）
            if (!_exclusive)
            {
                Guid iidVolume = WasapiTypes.IidISimpleAudioVolume;
                if (_client.GetService(&iidVolume, out IntPtr volPtr) == 0 && volPtr != IntPtr.Zero)
                {
                    _sessionVolumePtr = volPtr;
                    _sessionVolume = new RawSessionVolume(volPtr);
                }
            }

            _renderEvent = Win32.CreateEventW(IntPtr.Zero, false, false, null);
            _stopEvent = Win32.CreateEventW(IntPtr.Zero, true, false, null);
            if (_renderEvent == IntPtr.Zero || _stopEvent == IntPtr.Zero) return false;

            _pcmScratch = new float[_bufferFrames * source.Channels];
            _dopScratch = new uint[_bufferFrames * source.Channels];
            LatencyMs = (int)(_bufferFrames * 1000L / Math.Max(1, source.SampleRate));

            if (!_pushMode && _client.SetEventHandle(_renderEvent) != 0) return false;

            // 预置静音整块缓冲
            if (_render.GetBuffer(_bufferFrames, out byte* pre) == 0)
            {
                new Span<byte>(pre, (int)(_bufferFrames * _channels * BytesPerSample(_endpointKind))).Clear();
                _render.ReleaseBuffer(_bufferFrames, WasapiTypes.BufferFlagsSilent);
            }

            _thread = new Thread(RenderThreadProc)
            {
                IsBackground = true,
                Name = _exclusive ? "wasapi-excl-render" : "wasapi-render",
            };
            _thread.Start();
            int started = _client.Start();
            if (started != 0) Console.WriteLine($"[wasapi] Start hr=0x{started:X8}");
            return started == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[wasapi] Start exception: {ex.Message}");
            return false;
        }
    }

    public void Pause()
    {
        _pausedFlag = true;
        try { _client?.Stop(); } catch { }
    }

    public void Resume()
    {
        try { _client?.Start(); } catch { }
        _pausedFlag = false;
    }

    /// <summary>共享模式会话音量（0..1）。</summary>
    public void SetSessionVolume(float volume)
    {
        try { _sessionVolume?.SetMasterVolume(Math.Clamp(volume, 0f, 1f)); } catch { }
    }

    // ─────────────── 初始化与协商 ───────────────

    private bool InitializeShared(IRenderSource source, int requestedBufferFrames)
    {
        // 用端点混音格式初始化：虚拟声卡可能只接受精确混音格式（Senary 实测）。
        // 引擎侧已保证会话按混音率/声道重建，这里直接采用 GetMixFormat 原始指针。
        void** vtbl = *(void***)_clientPtr;
        var getMix = (delegate* unmanaged[Stdcall]<IntPtr, WAVEFORMATEX**, int>)vtbl[8];
        WAVEFORMATEX* mix = null;
        if (getMix(_clientPtr, &mix) != 0 || mix == null)
        {
            Console.WriteLine("[wasapi] GetMixFormat failed");
            return false;
        }

        int hr = InitializeNativeWithTimeout(WasapiTypes.ShareModeShared,
            WasapiTypes.StreamFlagsEventCallback | WasapiTypes.StreamFlagsNoPersist,
            3000000 /* 300ms 固定 */, 0, mix);
        if (hr != 0)
        {
            Console.WriteLine($"[wasapi] shared Initialize(mix) hr=0x{hr:X8} rate={mix->nSamplesPerSec} ch={mix->nChannels} bits={mix->wBitsPerSample}");
            if (hr != WasapiTypes.EPending) Win32.CoTaskMemFree((IntPtr)mix); // 超时路径 worker 仍持有，泄漏
            return false;
        }

        // 解析端点采样格式
        bool isFloat = mix->wFormatTag == 0x0003;
        if (mix->wFormatTag == 0xFFFE && mix->cbSize >= 22)
        {
            var ext = (WAVEFORMATEXTENSIBLE*)mix;
            isFloat = ext->SubFormat == SubFormats.IeeeFloat;
        }
        _endpointKind = isFloat ? FormatKind.Float32
            : mix->wBitsPerSample switch
            {
                16 => FormatKind.Pcm16,
                24 => FormatKind.Pcm24In32,
                _ => FormatKind.Pcm32,
            };
        _channels = mix->nChannels;
        int mix2Rate = (int)mix->nSamplesPerSec; // 释放前留存：诊断混音率是否随系统设置变化
        Win32.CoTaskMemFree((IntPtr)mix);

        int gbr = _client!.GetBufferSize(out _bufferFrames);
        if (gbr != 0)
        {
            Console.WriteLine($"[wasapi] GetBufferSize hr=0x{gbr:X8}");
            return false;
        }
        Console.WriteLine($"[wasapi] shared mix-initialized rate={_source.SampleRate} endpoint-rate={mix2Rate} buffer={_bufferFrames} kind={_endpointKind} ch={_channels}");
        return true;
    }

    /// <summary>共享模式：直接用原生 GetMixFormat 指针初始化（驱动持有期间指针有效）。</summary>
    private int InitializeNativeWithTimeout(int shareMode, int flags, long bufferDuration, long periodicity,
        WAVEFORMATEX* format)
    {
        var client = _client;
        if (client == null) return unchecked((int)0x80004005);
        int hr = unchecked((int)0x80004005);
        var worker = new Thread(() =>
        {
            try { hr = client.Initialize(shareMode, flags, bufferDuration, periodicity, format); }
            catch { hr = unchecked((int)0x80004005); }
        })
        { IsBackground = true, Name = "wasapi-init" };
        worker.Start();
        bool ok = worker.Join(3000);
        if (ok) return Volatile.Read(ref hr);
        // 超时：泄漏格式内存与线程（进程退出回收）
        _initTimedOut = true;
        return WasapiTypes.EPending;
    }

    private bool InitializeExclusive(IRenderSource source, int requestedBufferFrames)
    {
        int[] kinds = source.Kind == RenderKind.Dop
            ? new[] { FormatKind.Pcm24Packed, FormatKind.Pcm24In32, FormatKind.Pcm32 }
            : new[] { FormatKind.Float32, FormatKind.Pcm24In32, FormatKind.Pcm16, FormatKind.Pcm32 };

        foreach (int kind in kinds)
        {
            var format = MakeFormat(source, kind);
            long hns = (long)(10000000.0 * requestedBufferFrames / source.SampleRate + 0.5);
            int hr = InitializeExclusiveAligned(&format, hns);
            if (hr == 0 && _client!.GetBufferSize(out _bufferFrames) == 0)
            {
                _endpointKind = kind;
                _channels = (uint)source.Channels;
                Console.WriteLine($"[wasapi] exclusive format={kind} rate={source.SampleRate} buffer={_bufferFrames}");
                return true;
            }
            if (hr != 0)
                Console.WriteLine($"[wasapi] exclusive candidate kind={kind} hr=0x{hr:X8}");
            if (hr != WasapiTypes.AudclntEUnsupportedFormat && hr != WasapiTypes.AudclntEBufferSizeNotAligned)
                return false; // 设备占用/独占被拒等：换格式无意义
        }
        return false;
    }

    /// <summary>带对齐重试的独占初始化（ECHO initialize_exclusive_client 移植）。</summary>
    private int InitializeExclusiveAligned(WAVEFORMATEXTENSIBLE* format, long bufferHns)
    {
        // 推送模式不得带 EVENT_CALLBACK：带标志又不设事件句柄会让 Start 失败
        // （独占"不是独占"实际是被回退到共享的元凶）
        int flags = WasapiTypes.StreamFlagsNoPersist
            | (_pushMode ? 0 : WasapiTypes.StreamFlagsEventCallback);
        int hr = InitializeWithTimeout(WasapiTypes.ShareModeExclusive, flags, bufferHns, bufferHns, format);
        if (hr == WasapiTypes.AudclntEBufferSizeNotAligned)
        {
            // 对齐重试：GetBufferSize → 换算 hns → 再初始化
            if (_client!.GetBufferSize(out uint aligned) == 0 && aligned > 0)
            {
                long retry = (long)(10000000.0 * aligned / format->Format.nSamplesPerSec + 0.5);
                hr = InitializeWithTimeout(WasapiTypes.ShareModeExclusive, flags, retry, retry, format);
            }
        }
        return hr;
    }

    /// <summary>
    /// ECHO future-graveyard：Initialize 在专用线程执行，3 秒超时即放弃。
    /// 必须用 Thread.Join（不可内联）——Task.Wait 会把尚未开跑的任务体内联到
    /// 调用线程上，驱动挂死时“超时”永不触发（Senary 虚拟声卡实测会挂）。
    /// 超时后泄漏该次尝试的对象与线程，交给进程退出回收。
    /// </summary>
    private int InitializeWithTimeout(int shareMode, int flags, long bufferDuration, long periodicity, WAVEFORMATEXTENSIBLE* format)
    {
        // 格式放非托管内存：worker 线程持有指针；超时则泄漏（进程退出回收）。
        // （GCHandle.AddrOfPinnedObject 对装箱结构返回的是对象头，不可用。）
        var pFmt = (WAVEFORMATEXTENSIBLE*)NativeMemory.Alloc((nuint)sizeof(WAVEFORMATEXTENSIBLE));
        *pFmt = *format;
        var client = _client;
        if (client == null) { NativeMemory.Free(pFmt); return unchecked((int)0x80004005); }

        int hr = unchecked((int)0x80004005);
        var worker = new Thread(() =>
        {
            try { hr = client.Initialize(shareMode, flags, bufferDuration, periodicity, (WAVEFORMATEX*)pFmt); }
            catch { hr = unchecked((int)0x80004005); }
            NativeMemory.Free(pFmt); // 只有 worker 持有；完成后即释放
        })
        { IsBackground = true, Name = "wasapi-init" };
        worker.Start();
        return worker.Join(3000) ? Volatile.Read(ref hr) : WasapiTypes.EPending;
    }

    private static WAVEFORMATEXTENSIBLE MakeFormat(IRenderSource source, int kind) => kind switch
    {
        FormatKind.Float32 => WAVEFORMATEXTENSIBLE.Create((uint)source.SampleRate, (ushort)source.Channels, 32, 32, SubFormats.IeeeFloat),
        FormatKind.Pcm24In32 => WAVEFORMATEXTENSIBLE.Create((uint)source.SampleRate, (ushort)source.Channels, 32, 24, SubFormats.Pcm),
        FormatKind.Pcm32 => WAVEFORMATEXTENSIBLE.Create((uint)source.SampleRate, (ushort)source.Channels, 32, 32, SubFormats.Pcm),
        FormatKind.Pcm24Packed => WAVEFORMATEXTENSIBLE.Create((uint)source.SampleRate, (ushort)source.Channels, 24, 24, SubFormats.Pcm),
        FormatKind.Pcm16 => WAVEFORMATEXTENSIBLE.Create((uint)source.SampleRate, (ushort)source.Channels, 16, 16, SubFormats.Pcm),
        _ => default,
    };

    private static int BytesPerSample(int kind) => kind switch
    {
        FormatKind.Pcm16 => 2,
        FormatKind.Pcm24Packed => 3,
        _ => 4,
    };

    private static class FormatKind
    {
        public const int Float32 = 0;
        public const int Pcm24Packed = 1;
        public const int Pcm24In32 = 2;
        public const int Pcm32 = 3;
        public const int Pcm16 = 4;
    }

    // ─────────────── 渲染线程 ───────────────

    private void RenderThreadProc()
    {
        Win32.CoInitializeEx(IntPtr.Zero, Win32.COINIT_MULTITHREADED);
        uint mmcssIndex = 0;
        IntPtr avrt = Win32.AvSetMmThreadCharacteristicsW("Pro Audio", ref mmcssIndex);
        try
        {
            while (Win32.WaitForSingleObject(_stopEvent, 0) != 0)
            {
                if (_pausedFlag)
                {
                    Thread.Sleep(50);
                    continue;
                }
                uint frames;
                if (_pushMode)
                {
                    if (_client!.GetCurrentPadding(out uint padding) != 0) { Fail(); break; }
                    frames = _bufferFrames - padding;
                    if (frames == 0) { Thread.Sleep(2); continue; }
                }
                else
                {
                    int wait = Win32.WaitForSingleObject(_renderEvent, 2000);
                    if (wait != 0)
                    {
                        if (Win32.WaitForSingleObject(_stopEvent, 0) == 0) break;
                        if (_pausedFlag) continue; // 暂停后的超时唤醒：不消费
                        if (_client!.GetCurrentPadding(out uint pad) == 0 && _bufferFrames - pad == 0) continue;
                        Fail();
                        break;
                    }
                    if (_pausedFlag) continue; // 等待期间进入暂停：醒来后本轮不消费
                    if (_exclusive)
                    {
                        frames = _bufferFrames; // 独占事件模式：每周期整块可用
                    }
                    else
                    {
                        // 共享事件：按实际空闲量写
                        if (_client!.GetCurrentPadding(out uint pad2) != 0) { Fail(); break; }
                        frames = _bufferFrames - pad2;
                        if (frames == 0) continue;
                    }
                }

                int hr = _render!.GetBuffer(frames, out byte* dst);
                if (hr != 0) { Fail(); break; }

                try
                {
                    FillEndpoint(dst, frames);
                    if (Diagnostics.BufferDump.Enabled)
                        Diagnostics.BufferDump.Write(dst, (int)(frames * _channels * BytesPerSample(_endpointKind)));
                }
                catch
                {
                    new Span<byte>(dst, (int)(frames * _channels * BytesPerSample(_endpointKind))).Clear();
                    Fail();
                    _render.ReleaseBuffer(frames, WasapiTypes.BufferFlagsSilent);
                    break;
                }

                if (_render.ReleaseBuffer(frames, 0) != 0) { Fail(); break; }
            }
        }
        finally
        {
            if (avrt != IntPtr.Zero) Win32.AvRevertMmThreadCharacteristics(avrt);
            Win32.CoUninitialize();
        }
    }

    private void Fail() => Interlocked.Exchange(ref _failed, 1);

    private void FillEndpoint(byte* dst, uint frames)
    {
        long total = frames * _channels;
        switch (_source.Kind)
        {
            case RenderKind.Dop:
                _dopScratch.AsSpan(0, (int)total).Clear();
                _source.FillDop(_dopScratch, (int)frames);
                ConvertDopToEndpoint(_dopScratch, dst, (int)total, _endpointKind);
                break;
            default:
                _pcmScratch.AsSpan(0, (int)total).Clear();
                _source.FillPcm(_pcmScratch, (int)frames);
                ConvertFloatToEndpoint(_pcmScratch, dst, (int)total, _endpointKind);
                break;
        }
    }

    private static void ConvertFloatToEndpoint(float[] src, byte* dst, int total, int kind)
    {
        fixed (float* p = src)
        {
            switch (kind)
            {
                case FormatKind.Float32:
                    Buffer.MemoryCopy(p, dst, total * 4, total * 4);
                    break;
                case FormatKind.Pcm24In32:
                    {
                        int* d = (int*)dst;
                        for (int i = 0; i < total; i++)
                        {
                            float s = ClampSample(p[i]);
                            d[i] = (int)(s * 8388607f) << 8;
                        }
                        break;
                    }
                case FormatKind.Pcm24Packed:
                    {
                        for (int i = 0; i < total; i++)
                        {
                            float s = ClampSample(p[i]);
                            int v = (int)(s * 8388607f);
                            dst[i * 3] = (byte)(v & 0xff);
                            dst[i * 3 + 1] = (byte)((v >> 8) & 0xff);
                            dst[i * 3 + 2] = (byte)((v >> 16) & 0xff);
                        }
                        break;
                    }
                case FormatKind.Pcm32:
                    {
                        int* d = (int*)dst;
                        for (int i = 0; i < total; i++)
                            d[i] = (int)(ClampSample(p[i]) * 2147483647f);
                        break;
                    }
                case FormatKind.Pcm16:
                    {
                        short* d = (short*)dst;
                        for (int i = 0; i < total; i++)
                            d[i] = (short)(ClampSample(p[i]) * 32767f);
                        break;
                    }
            }
        }
    }

    private static void ConvertDopToEndpoint(uint[] src, byte* dst, int total, int kind)
    {
        fixed (uint* p = src)
        {
            switch (kind)
            {
                case FormatKind.Pcm24Packed:
                    for (int i = 0; i < total; i++)
                    {
                        uint sample = p[i] & 0x00ffffffu;
                        dst[i * 3] = (byte)(sample & 0xff);
                        dst[i * 3 + 1] = (byte)((sample >> 8) & 0xff);
                        dst[i * 3 + 2] = (byte)((sample >> 16) & 0xff);
                    }
                    break;
                case FormatKind.Pcm24In32:
                case FormatKind.Pcm32:
                    {
                        uint* d = (uint*)dst;
                        for (int i = 0; i < total; i++)
                            d[i] = p[i] << 8;
                        break;
                    }
            }
        }
    }

    private static float ClampSample(float s) => s > 1f ? 1f : s < -1f ? -1f : s;

    public void Dispose()
    {
        if (_stopEvent != IntPtr.Zero) Win32.SetEvent(_stopEvent);
        try { _thread?.Join(2000); } catch { }
        if (_initTimedOut)
        {
            // 墓园语义（ECHO future-graveyard）：Initialize worker 可能仍阻塞在驱动内部并持有
            // 该 client，任何 Stop/Reset/Release 都会与挂死线程并发使用同一 COM 对象 → 有意泄漏，
            // 交给进程退出回收。渲染/会话音量指针在成功初始化前不会取得，无需处理。
        }
        else
        {
            try { _client?.Stop(); } catch { }
            try { _client?.Reset(); } catch { }
            if (_renderPtr != IntPtr.Zero) { Marshal.Release(_renderPtr); _renderPtr = IntPtr.Zero; }
            if (_sessionVolumePtr != IntPtr.Zero) { Marshal.Release(_sessionVolumePtr); _sessionVolumePtr = IntPtr.Zero; }
            if (_clientPtr != IntPtr.Zero) { Marshal.Release(_clientPtr); _clientPtr = IntPtr.Zero; }
        }
        if (_devicePtr != IntPtr.Zero) { Marshal.Release(_devicePtr); _devicePtr = IntPtr.Zero; }
        if (_renderEvent != IntPtr.Zero) { Win32.CloseHandle(_renderEvent); _renderEvent = IntPtr.Zero; }
        if (_stopEvent != IntPtr.Zero) { Win32.CloseHandle(_stopEvent); _stopEvent = IntPtr.Zero; }
    }
}
