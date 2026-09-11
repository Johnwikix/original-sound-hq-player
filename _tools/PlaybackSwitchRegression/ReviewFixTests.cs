using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioPlayer;
using AudioPlayer.Interop;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static readonly ManualResetEventSlim ReviewInitializeContinue = new(false);
    private static int _reviewNativeCalls;

    private static void RunReviewFixTests(string root)
    {
        Run("Loudness: unknown measurement attenuates the first block and reports gain", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(new DspSettings { NormalizeLoudness = true });
            double[] samples = [1, 1];
            effects.ApplyInput(samples, 1);
            Require(Math.Abs(samples[0] - Math.Pow(10, -12.0 / 20)) < 1e-12, "first block used unity gain");
            Require(effects.GetState(0, false).GainDb == -12, "pending gain hidden from client");
            Set(effects, "_status", LoudnessStatus.Failed);
            Invoke(effects, "Publish");
            effects.ApplyInput(samples, 1);
            Require(effects.GetState(0, false).GainDb == -12, "failure removed attenuation");
            effects.Configure(new DspSettings { NormalizeLoudness = true, IsEnabled = false });
            Require(effects.GetState(0, false).GainDb == 0, "disabled DSP still claims attenuation");
        });
        Run("Loudness: measured gain rises smoothly and attenuation settles in 50ms", () =>
        {
            using var effects = new PcmEffects(48000, 2);
            effects.Configure(new DspSettings { NormalizeLoudness = true });
            var samples = new double[48000 * 2]; samples.AsSpan().Fill(1);
            effects.ApplyInput(samples.AsSpan(0, 2), 1);
            Set(effects, "_measurement", new LoudnessMeasurement(-24, 0.1));
            Invoke(effects, "Publish");
            samples.AsSpan().Fill(1); effects.ApplyInput(samples, 48000);
            Require(samples[0] < 0.26 && Math.Abs(samples[^1] - Math.Pow(10, 6.0 / 20)) < 1e-10, "gain increase jumped or never settled");
            Set(effects, "_measurement", new LoudnessMeasurement(-2, 1));
            Invoke(effects, "Publish");
            samples.AsSpan().Fill(1); effects.ApplyInput(samples.AsSpan(0, 4800), 2400);
            Require(Math.Abs(samples[4799] - Math.Pow(10, -16.0 / 20)) < 1e-10, "attenuation did not settle in 50ms");
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) effects.ApplyInput(samples, 48000);
            Require(GC.GetAllocatedBytesForCurrentThread() == allocated, "loudness render allocated");
        });
        Run("Loudness: cached results bypass a busy full-track scanner", () =>
        {
            string directory = Path.Combine(AppContext.BaseDirectory, "loudness-review-cache");
            string path = Path.Combine(root, "_tools/test_tone.wav");
            var measured = LoudnessScanner.ScanAsync(path, 44100, 2, 88200, 0, CancellationToken.None, directory).GetAwaiter().GetResult();
            Require(measured != null, "fixture measurement missing");
            var gate = (SemaphoreSlim)typeof(LoudnessScanner).GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            gate.Wait();
            using var timeout = new CancellationTokenSource(2000);
            try
            {
                var cached = LoudnessScanner.ScanAsync(path, 44100, 2, 88200, 0, timeout.Token, directory).GetAwaiter().GetResult();
                Require(cached == measured, "cache was blocked or changed");
            }
            finally { gate.Release(); }
        });
        Run("PCM: seek restarts decoding after EOF", () =>
        {
            using var session = Session.Open(Engine("WasapiShared"), Path.Combine(root, "_tools/test_tone.wav"), RenderKind.Pcm, 88200, 0, 300)!;
            var scratch = new double[8192];
            Require(SpinWait.SpinUntil(() => { session.FillPcm(scratch, 4096); return session.IsDrained; }, 5000), "EOF not reached");
            session.RequestSeek(0);
            Require(SpinWait.SpinUntil(() => session.ReadyFrames > 0, 2000), "seek did not restart PCM decoder");
        });
        Run("Seek: rejects a block decoded before reset and stale EOF", () =>
        {
            var ring = new PcmRing(1, 32, 0, 0);
            long epoch = ring.Epoch;
            ring.BeginSession();
            Require(!ring.Push([1.0], 1, static () => false, epoch), "stale block accepted");
            Require(!ring.MarkInputEnded(epoch) && !ring.IsDrained, "stale EOF accepted");
        });
        Run("Ring: consumption wakes a full producer without polling", () =>
        {
            var ring = new PcmRing(1, 1, 0, 0);
            ring.Push([1.0], 1, static () => false);
            var pushed = Task.Run(() => ring.Push([2.0], 1, static () => false));
            Thread.Sleep(20);
            Require(!pushed.IsCompleted, "producer should wait for capacity");
            ring.Render(new double[1], 1);
            Require(pushed.Wait(1000) && pushed.Result, "consumer failed to wake producer");
        });
        Run("Drain: final audio remains pending until device cycles consume it", () =>
        {
            var drain = new OutputDrainTracker();
            drain.BeginBlock(); drain.CompleteBlock(128, 128, 256);
            Require(!drain.IsDrained, "source EOF cannot mean device EOF");
            drain.BeginBlock(); drain.CompleteBlock(128, 0, 256);
            Require(!drain.IsDrained, "second ASIO buffer still pending");
            drain.BeginBlock(); drain.CompleteBlock(128, 0, 256);
            Require(drain.IsDrained, "tail never drained");
        });
        Run("Gain: initial mute installed before output can access session", () =>
        {
            var engine = Engine("WasapiShared");
            engine.OutputMode = "DirectSound";
            engine.Volume = 0;
            using var session = (Session)Invoke(engine, "OpenSession", Path.Combine(root, "_tools/test_tone.wav"), false, null)!;
            Require(session.Gain!.Target == 0, "new source still has unity gain");
            var scratch = new double[256];
            Require(SpinWait.SpinUntil(() => session.ReadyFrames >= 128, 2000), "no decoded data");
            session.FillPcm(scratch, 128);
            Require(scratch.All(x => x == 0), "first output block was not muted");
        });
        Run("Gain: concurrent updates settle on final target without render allocations", () =>
        {
            var gain = new GainRamp(48000);
            var samples = new double[512];
            var updater = Task.Run(() => { for (int i = 0; i < 10000; i++) gain.RampTo((i % 100) / 100.0, 1); });
            while (!updater.IsCompleted) { samples.AsSpan().Fill(1); gain.Apply(samples, 256, 2); }
            updater.GetAwaiter().GetResult();
            gain.SetImmediately(0); gain.RampTo(0.25, 1);
            samples.AsSpan().Fill(1); gain.Apply(samples, 256, 2);
            Require(samples[^1] == 0.25, "final gain target lost");
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) gain.Apply(samples, 256, 2);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "render allocated");
        });
        Run("EQ: identical control snapshots do not allocate", () =>
        {
            var eq = new Equalizer(); float[] gains = new float[10];
            eq.Configure(48000, true, gains);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++) eq.Configure(48000, true, gains);
            Require(GC.GetAllocatedBytesForCurrentThread() == before, "identical EQ values rebuilt snapshot");
        });
        Run("Settings: stable endpoint survives serialization independent of index", () =>
        {
            var settings = new IpcSetting { OutputMode = "WasapiShared", BassOutputDeviceId = 2, WasapiEndpointId = "{0.0.0.00000000}.{stable-endpoint}" };
            Span<byte> bytes = stackalloc byte[BinarySerializer.IpcSettingSize];
            int size = BinarySerializer.WriteIpcSetting(bytes, settings);
            var roundtrip = BinarySerializer.ReadIpcSetting(bytes[..size]);
            Require(roundtrip.WasapiEndpointId == settings.WasapiEndpointId, "endpoint lost");
        });
        Run("WASAPI: timed-out Initialize retires only after worker exits", CheckInitializeRetirement);
        Run("WASAPI: timed-out render retains client until thread exits", CheckRenderRetirement);
        Run("IPC: timeout preserves slot and coalescing respects ordered barriers", CheckMailboxOrdering);
        Run("IPC: cross-process confirmed roundtrip latency", BenchmarkConfirmedIpc);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ReviewInitialize(IntPtr self, int mode, int flags, long duration, long periodicity, WAVEFORMATEX* format, void* guid)
    { ReviewInitializeContinue.Wait(); return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int ReviewNativeCall(IntPtr self) { Interlocked.Increment(ref _reviewNativeCalls); return 0; }

    private static void CheckInitializeRetirement()
    {
        using var client = new NativeObject(15);
        client.Table[3] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int, int, long, long, WAVEFORMATEX*, void*, int>)&ReviewInitialize;
        client.Table[11] = client.Table[12] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ReviewNativeCall;
        var output = new WasapiOutput(false, false);
        Set(output, "_client", new RawAudioClient(client.Pointer));
        var format = WAVEFORMATEXTENSIBLE.Create(48000, 2, 32, 32, SubFormats.IeeeFloat);
        ReviewInitializeContinue.Reset(); _reviewNativeCalls = 0;
        try
        {
            int result = (int)Invoke(output, "InitializeWithTimeout", 0, 0, 1000000L, 0L,
                Pointer.Box(&format, typeof(WAVEFORMATEXTENSIBLE*)))!;
            Require(result == WasapiTypes.EPending, "worker did not time out");
            output.Dispose();
            Require(_reviewNativeCalls == 0, "Stop/Reset called during Initialize");
        }
        finally { ReviewInitializeContinue.Set(); }
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _reviewNativeCalls) == 2, 2000), "completed worker resources not retired");
    }

    private static void CheckRenderRetirement()
    {
        using var client = new NativeObject(15);
        client.Table[11] = client.Table[12] = (void*)(delegate* unmanaged[Stdcall]<IntPtr, int>)&ReviewNativeCall;
        var output = new WasapiOutput(false, false);
        Set(output, "_client", new RawAudioClient(client.Pointer));
        using var release = new ManualResetEventSlim(false);
        var render = new Thread(() => release.Wait()) { IsBackground = true };
        Set(output, "_thread", render);
        _reviewNativeCalls = 0; render.Start();
        try { output.Dispose(); Require(_reviewNativeCalls == 0, "client released before render exit"); }
        finally { release.Set(); render.Join(); }
        Require(SpinWait.SpinUntil(() => Volatile.Read(ref _reviewNativeCalls) == 2, 2000), "render retirement never completed");
    }

    private static void CheckMailboxOrdering()
    {
        using var mmf = MemoryMappedFile.CreateNew(null, IpcConstants.MmfSize);
        using var view = mmf.CreateViewAccessor();
        using var request = new Semaphore(0, 1); using var response = new Semaphore(0, 1);
        using var stop = new ManualResetEvent(false); using var entered = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var seen = new List<(CommandId Command, double Volume)>();
        var server = new Thread(() =>
        {
            WaitHandle[] waits = [stop, request]; byte[] payload = new byte[2048];
            while (WaitHandle.WaitAny(waits) != 0)
            {
                int version = IpcEnvelope.ReadVersion(view, IpcConstants.RequestVersionOffset);
                var command = IpcEnvelope.ReadCommandId(view, IpcConstants.RequestBufferOffset);
                int size = IpcEnvelope.ReadPayload(view, IpcConstants.RequestBufferOffset, payload, 2043);
                seen.Add((command, command == CommandId.ChangeVolume ? BinarySerializer.ReadChangeVolumeRequest(payload.AsSpan(0, size)).Volume : -1));
                if (seen.Count == 1) { entered.Set(); release.Wait(); }
                IpcEnvelope.WriteResponse(view, IpcConstants.ResponseBufferOffset, MessageTypeId.Success, (byte)version, [], 512);
                IpcEnvelope.PublishVersion(view, IpcConstants.ResponseVersionOffset, version);
                try { response.Release(); } catch (SemaphoreFullException) { }
            }
        }) { IsBackground = true };
        server.Start();
        using var client = new MailboxClient(view, request, response);
        try
        {
            byte[] payload = new byte[8];
            BinarySerializer.WriteChangeVolumeRequest(payload, new() { Volume = 0.1 });
            var first = client.RequestAsync(CommandId.ChangeVolume, payload, [], timeoutMs: 20);
            Require(entered.Wait(1000), "server did not read first command");
            Require(first.GetAwaiter().GetResult().Type == MessageTypeId.Failed, "caller timeout missing");
            for (int i = 0; i <= 10000; i++)
            {
                BinarySerializer.WriteChangeVolumeRequest(payload, new() { Volume = i / 10000.0 });
                Require(client.Publish(CommandId.ChangeVolume, payload, coalesce: true), "state queue overflow");
            }
            client.Publish(CommandId.Play, []);
            BinarySerializer.WriteChangeVolumeRequest(payload, new() { Volume = 0.3 });
            client.Publish(CommandId.ChangeVolume, payload, coalesce: true);
            var barrier = client.RequestAsync(CommandId.SetMusicUrl, [], []);
            Require(IpcEnvelope.ReadVersion(view, IpcConstants.RequestVersionOffset) == 1, "timeout overwrote in-flight request");
            release.Set();
            Require(barrier.GetAwaiter().GetResult().Type == MessageTypeId.Success, "queue did not recover");
            Require(seen.Count == 5 && seen[1].Volume == 1 && seen[2].Command == CommandId.Play && seen[3].Volume == 0.3,
                "coalescing dropped an ordered command or latest value");
        }
        finally { release.Set(); client.Dispose(); stop.Set(); server.Join(); }
    }

    private static int IpcBenchmarkServer(string suffix)
    {
        using var mmf = MemoryMappedFile.CreateNew("AudioReview-" + suffix, IpcConstants.MmfSize);
        using var view = mmf.CreateViewAccessor();
        using var request = new Semaphore(0, 1, "AudioReview-request-" + suffix);
        using var response = new Semaphore(0, 1, "AudioReview-response-" + suffix);
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, "AudioReview-stop-" + suffix);
        using var cancel = new CancellationTokenSource();
        var service = (PlayerIpcService)RuntimeHelpers.GetUninitializedObject(typeof(PlayerIpcService));
        var engine = Engine("DirectSound"); Set(engine, "_streamLock", new object());
        Set(service, "_engine", engine); Set(service, "_accessor", view); Set(service, "_requestReadySemaphore", request);
        Set(service, "_responseReadySemaphore", response); Set(service, "_requestBuffer", new byte[2048]);
        var listener = new Thread(() => Invoke(service, "ListenForRequests", cancel.Token)) { IsBackground = true };
        listener.Start(); stop.WaitOne(); cancel.Cancel(); listener.Join();
        return 0;
    }

    private static void BenchmarkConfirmedIpc()
    {
        string suffix = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.ArgumentList.Add("--ipc-server"); info.ArgumentList.Add(suffix);
        using var process = Process.Start(info)!;
        MemoryMappedFile? mmf = null; Semaphore? request = null, response = null; EventWaitHandle? stop = null;
        try
        {
            for (int i = 0; i < 100 && stop == null; i++)
            {
                try { stop = EventWaitHandle.OpenExisting("AudioReview-stop-" + suffix); }
                catch (WaitHandleCannotBeOpenedException) { Thread.Sleep(20); }
            }
            Require(stop != null, "benchmark server did not start");
            mmf = MemoryMappedFile.OpenExisting("AudioReview-" + suffix);
            request = Semaphore.OpenExisting("AudioReview-request-" + suffix);
            response = Semaphore.OpenExisting("AudioReview-response-" + suffix);
            using var view = mmf.CreateViewAccessor(); using var client = new MailboxClient(view, request, response);
            byte[] payload = new byte[8]; BinarySerializer.WriteChangeVolumeRequest(payload, new() { Volume = 0.5 });
            for (int i = 0; i < 200; i++) client.RequestAsync(CommandId.ChangeVolume, payload, []).GetAwaiter().GetResult();
            double[] elapsed = new double[5000]; var total = Stopwatch.StartNew();
            for (int i = 0; i < elapsed.Length; i++)
            {
                long begin = Stopwatch.GetTimestamp();
                Require(client.RequestAsync(CommandId.ChangeVolume, payload, []).GetAwaiter().GetResult().Type == MessageTypeId.Success, "roundtrip failed");
                elapsed[i] = Stopwatch.GetElapsedTime(begin).TotalMilliseconds;
            }
            total.Stop(); Array.Sort(elapsed);
            Console.WriteLine($"IPC confirmed cross-process: {elapsed.Length / total.Elapsed.TotalSeconds:F0}/s, p50={elapsed[2500]:F3}ms p95={elapsed[4750]:F3}ms p99={elapsed[4950]:F3}ms max={elapsed[^1]:F3}ms");
            Require(elapsed[4950] < 20, "p99 exceeds interactive setting budget (20ms)");
            // 真正服务端上的启动快照链：设置、EQ、DSP 均须先执行，再回读实际状态。
            byte[] settings = new byte[BinarySerializer.IpcSettingSize];
            byte[] eq = new byte[BinarySerializer.UpdateEqRequestSize];
            byte[] dsp = new byte[DspProtocol.SettingsSize];
            byte[] state = new byte[DspProtocol.StateSize];
            for (int i = 0; i < 100; i++)
            {
                bool enabled = (i & 1) == 0;
                int size = BinarySerializer.WriteIpcSetting(settings, new IpcSetting
                { OutputMode = "DirectSound", Volume = 0.5f, DsdPcmFreq = 88200, Latency = 300, IsEqualizerEnabled = enabled });
                BinarySerializer.WriteUpdateEqRequest(eq, new UpdateEqRequest { IsEnabled = enabled });
                DspProtocol.WriteSettings(dsp, new DspSettings { IsEnabled = true });
                Require(client.Publish(CommandId.UpdateSettings, settings.AsSpan(0, size)), "settings queue failed");
                Require(client.Publish(CommandId.UpdateEq, eq, true), "EQ queue failed");
                Require(client.Publish(CommandId.UpdateDsp, dsp, true), "DSP queue failed");
                var result = client.RequestAsync(CommandId.GetDspState, [], state).GetAwaiter().GetResult();
                Require(result.Type == MessageTypeId.DspState && result.Length == DspProtocol.StateSize
                    && DspProtocol.ReadState(state).EqualizerActive == enabled, "applied state differs from ordered settings");
            }
            Console.WriteLine("IPC cross-process: 100 Settings/EQ/DSP chains applied and read back correctly");
        }
        finally
        {
            stop?.Set();
            if (!process.WaitForExit(3000)) process.Kill();
            stop?.Dispose(); response?.Dispose(); request?.Dispose(); mmf?.Dispose();
        }
    }
}
