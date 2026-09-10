using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer.Interop;
using AudioPlayer.Playback;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]

// No physical output: real session selection and reuse paths, with controlled native vtables.
// Run from the repository root: dotnet run --project _tools/PlaybackSwitchRegression
internal static unsafe partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly ManualResetEventSlim PaddingEntered = new(false);
    private static readonly ManualResetEventSlim PaddingContinue = new(false);
    private static int _driverCalls, _releasedDuringPadding;
    private static double _rate;
    private static int _failures, _tests;

    // Test-only reflection seams also need metadata when validating NativeAOT interop.
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(PlaybackEngine))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(Session))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AsioOutput))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(AsioDriver))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(WasapiOutput))]
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        FFmpeg.AutoGen.ffmpeg.RootPath = AppContext.BaseDirectory;
        WriteDsfFixture();
        RunWavPackTests();
        RunBufferPolicyTests();
        foreach (string mode in new[] { "ASIO", "WasapiExclusivePush", "WasapiExclusiveEvent" })
        {
            Run($"{mode}: PCM -> DSD selects new file format", () =>
                CheckSession(root, mode, RenderKind.Pcm, "test_tone.dsf",
                    mode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop));
            Run($"{mode}: DSD -> PCM opens and decodes PCM", () =>
                CheckSession(root, mode, mode == "ASIO" ? RenderKind.NativeDsd : RenderKind.Dop,
                    "test_tone.wav", RenderKind.Pcm));
            Run($"{mode}: consecutive PCM rates decode and advance", () => CheckPcmSequence(root, mode));
            Run($"{mode}: disabling bitstream decodes DSD as PCM", () =>
                CheckSession(root, mode, RenderKind.Dop, "test_tone.dsf", RenderKind.Pcm, dopEnabled: false));
            Run($"{mode}: explicit DoP fallback is preserved", () =>
                CheckSession(root, mode, RenderKind.NativeDsd, "test_tone.dsf", RenderKind.Dop, kindOverride: RenderKind.Dop));
            CheckReuseMatrix(mode);
        }
        Run("ASIO: rate change leaves old buffers untouched for rebuild", CheckAsioRateChange);
        Run("WASAPI Push: rate change cannot release a client inside GetCurrentPadding", CheckWasapiRace);
        Console.WriteLine($"Playback switch regressions: {_tests - _failures}/{_tests} passed, {_failures} failure(s)");
        return _failures == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        _tests++;
        try { test(); Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failures++; Console.WriteLine($"FAIL {name}: {ex.GetBaseException().Message}"); }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Set(object target, string field, object? value) =>
        target.GetType().GetField(field, Private)!.SetValue(target, value);

    private static object? Invoke(object target, string name, params object?[] args) =>
        target.GetType().GetMethod(name, Private)!.Invoke(target, args);

    private static PlaybackEngine Engine(string mode)
    {
        // Bypass the constructor's endpoint listener/watchdog; all exercised methods are production code.
        var engine = (PlaybackEngine)RuntimeHelpers.GetUninitializedObject(typeof(PlaybackEngine));
        engine.OutputMode = mode;
        engine.IsDopEnabled = true;
        engine.DsdPcmFreq = 88200;
        engine.Latency = 300;
        engine.BassOutputDeviceId = -1;
        return engine;
    }

    private static Session Source(PlaybackEngine engine, RenderKind kind, int rate = 44100, int channels = 2) =>
        (Session)Activator.CreateInstance(typeof(Session), Private, null,
            [engine, kind, channels, rate, rate, 6000L, kind == RenderKind.Pcm ? rate : 0], null)!;

    private static void CheckSession(string root, string mode, RenderKind previous, string file, RenderKind expected,
        bool dopEnabled = true, RenderKind? kindOverride = null)
    {
        var engine = Engine(mode);
        engine.IsDopEnabled = dopEnabled;
        using var old = Source(engine, previous);
        Set(engine, "_session", old);
        engine.MusicUrl = file.EndsWith(".dsf", StringComparison.Ordinal)
            ? Path.Combine(AppContext.BaseDirectory, "switch-test.dsf")
            : Path.Combine(root, "_tools", file);
        using var next = (Session?)Invoke(engine, "OpenSession", engine.MusicUrl, false, kindOverride);
        Require(next != null, $"new {file} session failed to open after {previous}");
        Require(next!.Kind == expected, $"expected {expected}, got {next.Kind} (previous={previous})");

        CheckProgress(next);
    }

    private static void CheckProgress(Session next)
    {
        int frames = Math.Max(1, next.SampleRate / (next.Kind == RenderKind.NativeDsd ? 800 : 100));
        var pcm = new double[frames * next.Channels];
        var dop = new uint[frames * next.Channels];
        var dsd = new byte[frames * next.Channels];
        for (int i = 0; i < 200 && next.CurrentMs == 0; i++)
        {
            if (next.Kind == RenderKind.Pcm) next.FillPcm(pcm, frames);
            else if (next.Kind == RenderKind.Dop) next.FillDop(dop, frames);
            else next.FillDsdBytes(dsd, frames);
            Thread.Sleep(5);
        }
        Require(next.CurrentMs > 0, "new source never advances playback");
    }

    private static void CheckPcmSequence(string root, string mode)
    {
        var engine = Engine(mode);
        Session? previous = null;
        try
        {
            foreach (var (file, rate) in new[]
            {
                ("test_tone.wav", 44100), ("test_tone_48000.wav", 48000),
                ("test_tone_96000.wav", 96000), ("test_tone.wav", 44100),
            })
            {
                engine.MusicUrl = Path.Combine(root, "_tools", file);
                var next = (Session?)Invoke(engine, "OpenSession", engine.MusicUrl, false, null);
                Require(next != null, $"failed to open {file}");
                previous?.Dispose();
                previous = next;
                Set(engine, "_session", next);
                Require(next!.Kind == RenderKind.Pcm && next.SampleRate == rate, $"incorrect format for {file}");
                CheckProgress(next);
            }
        }
        finally { previous?.Dispose(); }
    }

    private static void CheckReuseMatrix(string mode)
    {
        foreach (var (name, previousKind, nextKind, rate, channels, failed, deviceChanged, modeChanged, expected) in new[]
        {
            ("same PCM format", RenderKind.Pcm, RenderKind.Pcm, 44100, 2, false, false, false, true),
            ("48 kHz PCM", RenderKind.Pcm, RenderKind.Pcm, 48000, 2, false, false, false, false),
            ("96 kHz PCM", RenderKind.Pcm, RenderKind.Pcm, 96000, 2, false, false, false, false),
            ("mono PCM", RenderKind.Pcm, RenderKind.Pcm, 44100, 1, false, false, false, false),
            ("PCM to DoP at same carrier rate", RenderKind.Pcm, RenderKind.Dop, 44100, 2, false, false, false, false),
            ("DoP to PCM at same carrier rate", RenderKind.Dop, RenderKind.Pcm, 44100, 2, false, false, false, false),
            ("PCM to native DSD", RenderKind.Pcm, RenderKind.NativeDsd, 44100, 2, false, false, false, false),
            ("failed output", RenderKind.Pcm, RenderKind.Pcm, 44100, 2, true, false, false, false),
            ("different device", RenderKind.Pcm, RenderKind.Pcm, 44100, 2, false, true, false, false),
            ("different output mode", RenderKind.Pcm, RenderKind.Pcm, 44100, 2, false, false, true, false),
        })
        {
            Run($"{mode}: reuse policy for {name}", () =>
            {
                using var native = new NativeObject(24);
                var engine = Engine(mode);
                using var old = Source(engine, previousKind);
                using var next = Source(engine, nextKind, rate, channels);
                IAudioOutput output;
                if (mode == "ASIO")
                {
                    output = new AsioOutput();
                    var driver = (AsioDriver)Activator.CreateInstance(typeof(AsioDriver), Private, null, [native.Pointer], null)!;
                    Set(output, "_driver", driver);
                    Set(output, "<DeviceIndex>k__BackingField", 0);
                    if (deviceChanged) engine.BassASIODeviceId = 1;
                    if (modeChanged) engine.OutputMode = "WasapiExclusiveEvent";
                }
                else
                {
                    output = new WasapiOutput(true, mode == "WasapiExclusivePush");
                    Set(output, "_client", new RawAudioClient(native.Pointer));
                    if (deviceChanged) engine.BassOutputDeviceId = 1;
                    if (modeChanged) engine.OutputMode = mode == "WasapiExclusivePush" ? "WasapiExclusiveEvent" : "ASIO";
                }
                Set(output, "_source", old);
                Set(output, "_failed", failed ? 1 : 0);
                Set(engine, "_output", output);
                Require(Reuse(engine, next) == expected, $"expected reuse={expected}");
                Require(ReferenceEquals(output.GetType().GetField("_source", Private)!.GetValue(output), expected ? next : old),
                    "reuse decision replaced the wrong source");

                // The output API must also reject incompatible sources if called without the engine gate.
                Set(output, "_source", old);
                bool attached = output is AsioOutput asio ? asio.AttachSource(next) : ((WasapiOutput)output).AttachSource(next);
                bool expectedAttach = !failed && previousKind == RenderKind.Pcm && nextKind == RenderKind.Pcm
                    && rate == 44100 && channels == 2;
                Require(attached == expectedAttach, $"AttachSource expected={expectedAttach}, actual={attached}");
            });
        }
    }

    private static bool Reuse(PlaybackEngine engine, Session next) =>
        (bool)Invoke(engine, "TryReuseExclusiveOutput", next)!;

    private static void WriteDsfFixture()
    {
        // Self-contained DSD64 stereo silence. The legacy _tools/test_tone.dsf has an
        // incomplete DSD header, so it cannot validate either FFmpeg decoding path.
        const int channelBytes = 4096 * 128;
        using var writer = new BinaryWriter(File.Create(Path.Combine(AppContext.BaseDirectory, "switch-test.dsf")));
        writer.Write("DSD "u8);
        writer.Write(28L);
        writer.Write(28L + 52 + 12 + channelBytes * 2);
        writer.Write(0L); // metadata pointer (required even with no ID3 chunk)
        writer.Write("fmt "u8);
        writer.Write(52L);
        writer.Write(1); // version
        writer.Write(0); // raw DSD
        writer.Write(2); // stereo channel type
        writer.Write(2); // channels
        writer.Write(2822400);
        writer.Write(1); // LSBF
        writer.Write(channelBytes * 8L);
        writer.Write(4096);
        writer.Write(0);
        writer.Write("data"u8);
        writer.Write(12L + channelBytes * 2);
        byte[] block = new byte[4096];
        Array.Fill(block, (byte)0x69);
        for (int i = 0; i < channelBytes * 2 / block.Length; i++) writer.Write(block);
    }

    private static void CheckAsioRateChange()
    {
        using var native = new NativeObject(24);
        native.Table[7] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&DriverCall;
        native.Table[8] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&DriverCall;
        native.Table[12] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, double, int>)&CanRate;
        native.Table[13] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, double*, int>)&GetRate;
        native.Table[14] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, double, int>)&SetRate;
        var driver = (AsioDriver)Activator.CreateInstance(typeof(AsioDriver), Private, null, [native.Pointer], null)!;
        var engine = Engine("ASIO");
        using var old = Source(engine, RenderKind.Pcm);
        using var next = Source(engine, RenderKind.Pcm, 96000);
        var output = new AsioOutput();
        Set(output, "_source", old);
        Set(output, "_driver", driver);
        Set(output, "_started", true);
        Set(output, "<DeviceIndex>k__BackingField", 0);
        Set(engine, "_output", output);
        _driverCalls = 0;
        bool reused = Reuse(engine, next);
        Require(!reused && _driverCalls == 0 && output.SourceSampleRate == 44100,
            $"reused={reused}, driver calls={_driverCalls}, old buffers now at {output.SourceSampleRate} Hz");
    }

    private static void CheckWasapiRace()
    {
        using var client = new NativeObject(15);
        using var render = new NativeObject(5);
        using var device = new NativeObject(4);
        client.Table[2] = render.Table[2] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        client.Table[6] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetPadding;
        client.Table[11] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&DriverCall;
        device.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, int, void*, IntPtr*, int>)&Activate;
        var engine = Engine("WasapiExclusivePush");
        using var old = Source(engine, RenderKind.Pcm);
        using var next = Source(engine, RenderKind.Pcm, 96000);
        var output = new WasapiOutput(true, true);
        Set(output, "_source", old);
        Set(output, "_client", new RawAudioClient(client.Pointer));
        Set(output, "_clientPtr", client.Pointer);
        Set(output, "_render", new RawRenderClient(render.Pointer));
        Set(output, "_renderPtr", render.Pointer);
        Set(output, "_devicePtr", device.Pointer);
        Set(output, "_bufferFrames", 441u);
        IntPtr stop = Win32.CreateEventW(IntPtr.Zero, true, false, null);
        Set(output, "_stopEvent", stop);
        Set(engine, "_output", output);
        PaddingEntered.Reset();
        PaddingContinue.Reset();
        _releasedDuringPadding = 0;
        var thread = new Thread(() => Invoke(output, "RenderThreadProc")) { IsBackground = true };
        thread.Start();
        try
        {
            Require(PaddingEntered.Wait(2000), "render thread did not enter padding query");
            Require(!Reuse(engine, next), "changed format must rebuild");
            Require(_releasedDuringPadding == 0, $"released {_releasedDuringPadding} COM objects while padding query was in flight");
        }
        finally
        {
            Win32.SetEvent(stop);
            PaddingContinue.Set();
            Require(thread.Join(2000), "render thread failed to stop");
            Win32.CloseHandle(stop);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DriverCall(IntPtr self) { Interlocked.Increment(ref _driverCalls); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CanRate(IntPtr self, double rate) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SetRate(IntPtr self, double rate) { _rate = rate; Interlocked.Increment(ref _driverCalls); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetRate(IntPtr self, double* rate) { *rate = _rate; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(IntPtr self)
    {
        if (PaddingEntered.IsSet && !PaddingContinue.IsSet) Interlocked.Increment(ref _releasedDuringPadding);
        return 1;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPadding(IntPtr self, uint* padding)
    {
        PaddingEntered.Set();
        PaddingContinue.Wait(5000);
        *padding = 441;
        return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Activate(IntPtr self, Guid* iid, int context, void* options, IntPtr* client)
    {
        *client = IntPtr.Zero;
        return unchecked((int)0x80004005);
    }

    private sealed class NativeObject : IDisposable
    {
        public readonly void** Table;
        public readonly IntPtr Pointer;
        public NativeObject(int slots)
        {
            Table = (void**)NativeMemory.AllocZeroed((nuint)(slots * IntPtr.Size));
            Pointer = (IntPtr)NativeMemory.Alloc((nuint)IntPtr.Size);
            *(void***)Pointer = Table;
        }
        public void Dispose() { NativeMemory.Free((void*)Pointer); NativeMemory.Free(Table); }
    }
}
