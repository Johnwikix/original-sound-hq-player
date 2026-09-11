using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;

internal static unsafe partial class Program
{
    private static WasapiOutput? _observedOutput;
    private static int _startCalls, _startResult, _stopResult;
    private static bool _startedWhilePaused;
    private static IntPtr _retryOriginal, _retryReplacement;
    private static int _retryInitialResult, _retryReleaseCount, _retryActivateCount;
    private static long _retryDuration;

    private static void RunWasapiStallTests()
    {
        Run("WASAPI: live source attachment never restarts a running client", () =>
        {
            using var native = new NativeObject(15);
            native.Table[10] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ObserveStart;
            var output = new WasapiOutput(true, false);
            _observedOutput = output; _startCalls = 0; _startResult = 0;
            Set(output, "_client", new RawAudioClient(native.Pointer));
            var engine = Engine("WasapiExclusiveEvent");
            using var source = Source(engine, RenderKind.Pcm);
            Set(engine, "_session", source); Set(engine, "_output", output);
            engine.IsPlaying = true;
            Invoke(engine, "AfterAttachToLiveOutput", true);
            Require(_startCalls == 0, "live attachment issued another native Start");
        });
        Run("WASAPI: resume opens render gate before native Start can signal", () =>
        {
            using var native = new NativeObject(15);
            native.Table[10] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ObserveStart;
            var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer)); Set(output, "_pausedFlag", true);
            _observedOutput = output; _startCalls = 0; _startResult = 0; _startedWhilePaused = false;
            output.Resume(); output.Resume();
            Require(!_startedWhilePaused && _startCalls == 1, "first event could be dropped or Start repeated");
        });
        Run("WASAPI: failed resume marks output failed for engine recovery", () =>
        {
            using var native = new NativeObject(15);
            native.Table[10] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ObserveStart;
            var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer)); Set(output, "_pausedFlag", true);
            _observedOutput = output; _startResult = unchecked((int)0x88890004);
            output.Resume();
            Require(output.IsFailed, "failed Start was hidden from recovery");
        });
        Run("WASAPI: failed pause marks output failed", () =>
        {
            using var native = new NativeObject(15);
            native.Table[11] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ObserveStop;
            var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer));
            _stopResult = unchecked((int)0x88890004); output.Pause();
            Require(output.IsFailed, "failed Stop was hidden");
        });
        Run("WASAPI event: missing events with full padding must fail", () => CheckStalledWasapi(false));
        Run("WASAPI push: permanent full padding must fail", () => CheckStalledWasapi(true));
        Run("WASAPI shared: missing events must fail", () => CheckStalledWasapi(false, false));
        Run("WASAPI: intentional pause is healthy and resume receives a fresh deadline", () =>
        {
            using var native = new NativeObject(15);
            native.Table[10] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ObserveStart;
            var output = new WasapiOutput(true, false);
            Set(output, "_client", new RawAudioClient(native.Pointer)); Set(output, "_pausedFlag", true);
            _observedOutput = output; _startResult = 0;
            IntPtr stop = Win32.CreateEventW(IntPtr.Zero, true, false, null);
            IntPtr ready = Win32.CreateEventW(IntPtr.Zero, false, false, null);
            Set(output, "_stopEvent", stop); Set(output, "_renderEvent", ready);
            var thread = new Thread(() => Invoke(output, "RenderThreadProc")) { IsBackground = true };
            thread.Start();
            try
            {
                Thread.Sleep(2200);
                Require(!output.IsFailed, "intentional pause was treated as a stalled driver");
                output.Resume(); Thread.Sleep(200);
                Require(!output.IsFailed, "resume inherited the expired pause deadline");
            }
            finally { Win32.SetEvent(stop); Require(thread.Join(2000), "paused render did not exit"); Win32.CloseHandle(stop); Win32.CloseHandle(ready); }
        });
        Run("WASAPI: aligned retry uses a fresh client at the new sample rate", () => CheckFreshInitialize(true));
        Run("WASAPI: rejected format uses a fresh client for next candidate", () => CheckFreshInitialize(false));
    }

    private static void CheckStalledWasapi(bool push, bool exclusive = true)
    {
        using var native = new NativeObject(15);
        native.Table[6] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&FullPadding;
        var output = new WasapiOutput(exclusive, push);
        Set(output, "_client", new RawAudioClient(native.Pointer)); Set(output, "_bufferFrames", 1920u);
        IntPtr stop = Win32.CreateEventW(IntPtr.Zero, true, false, null);
        IntPtr ready = Win32.CreateEventW(IntPtr.Zero, false, false, null);
        Set(output, "_stopEvent", stop); Set(output, "_renderEvent", ready);
        var thread = new Thread(() => Invoke(output, "RenderThreadProc")) { IsBackground = true };
        thread.Start();
        try { Require(SpinWait.SpinUntil(() => output.IsFailed, 3200), "stalled stream remained healthy forever"); }
        finally { Win32.SetEvent(stop); Require(thread.Join(2000), "render did not exit"); Win32.CloseHandle(stop); Win32.CloseHandle(ready); }
    }

    private static void CheckFreshInitialize(bool alignment)
    {
        using var original = new NativeObject(15); using var replacement = new NativeObject(15); using var device = new NativeObject(4);
        original.Table[2] = replacement.Table[2] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ObserveRetryRelease;
        original.Table[3] = replacement.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)&RetryInitialize;
        original.Table[4] = replacement.Table[4] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&AlignedSize;
        device.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, void*, IntPtr*, int>)&RetryActivate;
        _retryOriginal = original.Pointer; _retryReplacement = replacement.Pointer;
        _retryInitialResult = alignment ? WasapiTypes.AudclntEBufferSizeNotAligned : WasapiTypes.AudclntEUnsupportedFormat;
        _retryReleaseCount = _retryActivateCount = 0; _retryDuration = 0;
        var output = new WasapiOutput(true, false);
        Set(output, "_client", new RawAudioClient(original.Pointer)); Set(output, "_clientPtr", original.Pointer); Set(output, "_devicePtr", device.Pointer);
        using var source = Source(Engine("WasapiExclusiveEvent"), RenderKind.Pcm, 48000);
        bool result = (bool)Invoke(output, "InitializeExclusive", source, 1800)!;
        Require(result && _retryReleaseCount == 1 && _retryActivateCount == 1, "failed client was reused for initialization");
        Require(_retryDuration == (alignment ? 400000 : 375000), "retry duration did not use the new rate/aligned frames");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ObserveStart(IntPtr self)
    {
        _startCalls++;
        _startedWhilePaused = (bool)typeof(WasapiOutput).GetField("_pausedFlag", Private)!.GetValue(_observedOutput)!;
        return _startResult;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ObserveStop(IntPtr self) => _stopResult;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FullPadding(IntPtr self, uint* frames) { *frames = 1920; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int AlignedSize(IntPtr self, uint* frames) { *frames = 1920; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ObserveRetryRelease(IntPtr self) { if (self == _retryOriginal) _retryReleaseCount++; return 1; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RetryActivate(IntPtr self, Guid* iid, int context, void* parameters, IntPtr* result)
    { _retryActivateCount++; *result = _retryReplacement; return _retryReleaseCount == 1 ? 0 : unchecked((int)0x80004005); }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RetryInitialize(IntPtr self, int mode, int flags, long duration, long period, WAVEFORMATEX* format, void* session)
    {
        if (self == _retryOriginal) return _retryInitialResult;
        _retryDuration = duration;
        return format->nSamplesPerSec == 48000 && duration == period ? 0 : unchecked((int)0x80004005);
    }
}
