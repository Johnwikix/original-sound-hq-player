using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;

internal static unsafe partial class Program
{
    private static AsioCallbacks _asioCallbacks;
    private static int _bufferDisposeCalls;

    private static void RunAsioNotificationTests()
    {
        foreach (int selector in new[] { 3, 4, 5 })
            Run($"ASIO: driver message {selector} invalidates same-rate track reuse without driver calls", () =>
                WithAsioOutput((output, engine) =>
                {
                    _driverCalls = 0;
                    int result = ((delegate* unmanaged[Stdcall]<int, int, void*, double*, int>)_asioCallbacks.AsioMessage)(selector, 1024, null, null);
                    using var next = Source(engine, RenderKind.Pcm);
                    Require(result == 1 && output.IsFailed && !Reuse(engine, next) && _driverCalls == 0,
                        $"result={result}, failed={output.IsFailed}, driver calls={_driverCalls}");
                }));
        Run("ASIO: external rate change invalidates old buffers", () => WithAsioOutput((output, engine) =>
        {
            ((delegate* unmanaged[Stdcall]<double, void>)_asioCallbacks.SampleRateDidChange)(96000);
            using var next = Source(engine, RenderKind.Pcm);
            Require(output.IsFailed && !Reuse(engine, next), "changed device reused stale 44100 Hz buffers");
        }));
        Run("ASIO: same-rate notification does not create reset loop", () => WithAsioOutput((output, _) =>
        {
            ((delegate* unmanaged[Stdcall]<double, void>)_asioCallbacks.SampleRateDidChange)(44100);
            Require(!output.IsFailed, "host negotiation was treated as external rate change");
        }));
        Run("ASIO: ordinary and time-info buffer callbacks are both registered", () => WithAsioOutput((output, _) =>
            Require(_asioCallbacks.BufferSwitch != IntPtr.Zero && _asioCallbacks.BufferSwitchTimeInfo != IntPtr.Zero,
                "ordinary bufferSwitch is missing")));
        Run("ASIO: absent callbacks invalidate a running output but not a paused one", () => WithAsioOutput((output, _) =>
        {
            Set(output, "_running", 1);
            Set(output, "_lastCallbackTick", Environment.TickCount64 - 10000);
            Require(output.IsFailed, "silent callback stall was not detected");
            Set(output, "_running", 0);
            Require(!output.IsFailed, "paused output was treated as stalled");
        }));
        Run("ASIO: disposal waits for an in-flight source callback before releasing buffers", CheckAsioCallbackDisposal);
        Run("ASIO: watchdog retires reset output and schedules cancellable recovery", () => WithAsioOutput((output, engine) =>
        {
            Set(engine, "_streamLock", new object());
            using var session = Source(engine, RenderKind.Pcm);
            Set(engine, "_session", session);
            engine.IsPlaying = true;
            Set(engine, "_fastFailStreak", 1);
            ((delegate* unmanaged[Stdcall]<int, int, void*, double*, int>)_asioCallbacks.AsioMessage)(3, 0, null, null);
            Invoke(engine, "WatchdogTick");
            Require(ReferenceEquals(typeof(PlaybackEngine).GetField("_output", Private)!.GetValue(engine), output),
                "reset notification was not debounced");
            Set(output, "_restartTick", Environment.TickCount64 - 1000);
            Invoke(engine, "WatchdogTick");
            var plan = typeof(PlaybackEngine).GetField("_recovery", Private)!.GetValue(engine);
            Require(plan != null && engine.IsPlaying && typeof(PlaybackEngine).GetField("_output", Private)!.GetValue(engine) == null,
                "watchdog did not retire old buffers and schedule recovery");
            Require((int)typeof(PlaybackEngine).GetField("_fastFailStreak", Private)!.GetValue(engine)! == 0,
                "configuration change counted as rapid device failure");
            Set(engine, "_playGen", 1L);
            Invoke(engine, "WatchdogTick");
            Require(typeof(PlaybackEngine).GetField("_recovery", Private)!.GetValue(engine) == null,
                "new user operation did not cancel stale recovery");
        }));
        Run("ASIO: reset while paused stays paused and prevents later reuse", () => WithAsioOutput((output, engine) =>
        {
            Set(engine, "_streamLock", new object());
            ((delegate* unmanaged[Stdcall]<int, int, void*, double*, int>)_asioCallbacks.AsioMessage)(4, 512, null, null);
            Invoke(engine, "WatchdogTick");
            Require(!engine.IsPlaying && output.IsFailed, "paused reset lost pending state or started playback");
        }));
        Run("ASIO: ordinary buffer callback supplies audio and updates heartbeat", () => WithAsioOutput((output, _) =>
        {
            var source = new BlockingAsioSource();
            source.Continue.Set();
            Set(output, "_source", source);
            Set(output, "_postOutput", false);
            Set(output, "_running", 1);
            Set(output, "_lastCallbackTick", Environment.TickCount64 - 10000);
            ((delegate* unmanaged[Stdcall]<int, int, void>)_asioCallbacks.BufferSwitch)(0, 0);
            Require(source.Entered.IsSet && !output.IsFailed, "ordinary callback did not render/update heartbeat");
        }));
    }

    private static void WithAsioOutput(Action<AsioOutput, PlaybackEngine> test)
    {
        using var native = new NativeObject(24);
        native.Table[2] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        native.Table[7] = native.Table[8] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&DriverCall;
        native.Table[19] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, AsioBufferInfo*, int, int, AsioCallbacks*, int>)&CaptureAsioCallbacks;
        native.Table[20] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&DisposeAsioBuffers;
        var driver = (AsioDriver)Activator.CreateInstance(typeof(AsioDriver), Private, null, [native.Pointer], null)!;
        var engine = Engine("ASIO");
        using var source = Source(engine, RenderKind.Pcm);
        using var output = new AsioOutput();
        Set(output, "_driver", driver);
        Set(output, "_source", source);
        Set(output, "_dsdRateDomain", 44100);
        Set(output, "<DeviceIndex>k__BackingField", 0);
        Set(engine, "_output", output);
        Require((bool)Invoke(output, "TryCreateBuffers", 1024, false)!, "fake driver did not create buffers");
        typeof(AsioOutput).GetField("_active", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, output);
        test(output, engine);
    }

    private static void CheckAsioCallbackDisposal() => WithAsioOutput((output, _) =>
    {
        var source = new BlockingAsioSource();
        Set(output, "_source", source);
        Set(output, "_postOutput", false);
        _bufferDisposeCalls = 0;
        IntPtr callback = _asioCallbacks.BufferSwitchTimeInfo;
        var render = new Thread(() => ((delegate* unmanaged[Stdcall]<void*, int, int, void*>)callback)(null, 0, 0));
        render.Start();
        Require(source.Entered.Wait(2000), "source callback not entered");
        var dispose = new Thread(output.Dispose);
        try
        {
            dispose.Start();
            Require(!dispose.Join(100) && Volatile.Read(ref _bufferDisposeCalls) == 0,
                "driver buffers released while render callback still owns them");
        }
        finally
        {
            source.Continue.Set();
            Require(render.Join(2000) && dispose.Join(2000), "render/dispose did not finish");
        }
    });

    private sealed class BlockingAsioSource : IRenderSource
    {
        public readonly ManualResetEventSlim Entered = new(false), Continue = new(false);
        public RenderKind Kind => RenderKind.Pcm;
        public int SampleRate => 44100;
        public int Channels => 2;
        public void FillPcm(Span<double> buffer, int frames) { Entered.Set(); Continue.Wait(3000); }
        public void FillDop(Span<uint> buffer, int frames) { }
        public void FillDsdBytes(Span<byte> buffer, int frames) { }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CaptureAsioCallbacks(IntPtr self, AsioBufferInfo* buffers, int channels, int size, AsioCallbacks* callbacks)
    { _asioCallbacks = *callbacks; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DisposeAsioBuffers(IntPtr self) { Interlocked.Increment(ref _bufferDisposeCalls); return 0; }
}
