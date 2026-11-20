using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static GaplessPreloader Preloader(PlaybackEngine engine) =>
        (GaplessPreloader)typeof(PlaybackEngine).GetField("_gaplessPreloader", Private)!.GetValue(engine)!;

    private static void RunGaplessLifecycleTests()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "gapless-lifecycle.wav");
        WriteLoudnessTone(path, 0.1f);
        Run("Gapless: incompatible PCM is rejected before allocating a standby session", () => CheckStandbyFormatAllocation(path));
        Run("Gapless: EOF and terminal decoder failure retire unused standbys", CheckTerminalStandbyRetirement);
        Run("Gapless: steady-state progress does not wait for the render gate", CheckProgressDoesNotWaitForRender);
        Run("Gapless: wall-clock window, seek away, unknown duration and disabled preparation", () =>
        {
            foreach (double rate in new[] { 0.25, 1, 5 })
            {
                var engine = Engine("WasapiShared");
                Set(engine, "_streamLock", new object());
                Set(engine, "_dspSettings", new DspSettings { PlaybackRate = rate });
                var first = BufferedSource(engine, [0.1, 0.1]);
                Set(first, "<TotalMs>k__BackingField", 60_000L);
                Set(first, "<PlaybackRate>k__BackingField", rate);
                var source = new GaplessSource(first);
                Set(engine, "_session", first);
                Set(engine, "_gaplessSource", source);
                engine.IsPlaying = true;
                try
                {
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 1, path)), "Plan not accepted");
                    var preloader = Preloader(engine);
                    Require(preloader.State == GaplessPreparationState.Waiting && preloader.Completion.IsCompleted,
                        "Long track opened next decoder at its start");
                    // The watchdog check itself must not allocate a work item or a request snapshot.
                    var tick = (Action)typeof(PlaybackEngine).GetMethod("PrepareGaplessIfDue", Private)!.CreateDelegate(typeof(Action), engine);
                    tick();
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < 1000; i++) tick();
                    Require(GC.GetAllocatedBytesForCurrentThread() == before, "Waiting-window checks allocated");
                    first.AnchorFrames = first.MsToFrames((long)(60_000 - 9_000 * rate));
                    tick();
                    Require(preloader.Completion.Wait(5000) && source.Pending != null, "Did not preload inside real-time window");
                    engine.ChangeWaveChannelTime(0);
                    Require(source.Pending == null, "Seek away retained pending source");
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 2, path)), "New seek epoch rejected");
                    Require(preloader.State == GaplessPreparationState.Waiting, "Seek away reopened next decoder");
                    engine.UpdateDsp(new DspSettings { GaplessPlayback = false, PlaybackRate = rate });
                    tick();
                    Require(preloader.Request == null && source.Pending == null, "Disabled gapless kept preparation active");
                    Set(first, "<TotalMs>k__BackingField", 0L);
                    engine.UpdateDsp(new DspSettings { PlaybackRate = rate });
                    Require(engine.TryQueueNext(new(first.TimelineEpoch, 3, path)), "Unknown-duration plan rejected");
                    Require(preloader.Completion.Wait(5000) && source.Pending != null, "Unknown duration lost early preparation");
                }
                finally
                {
                    engine.StopGaplessAsync().GetAwaiter().GetResult();
                    Invoke(engine, "DisposeSession");
                }
            }
        });

        Run("Gapless: DSP, correction and preview updates retain the prepared decoder and ring", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            var settings = new DspSettings { AutoPreamp = false };
            Set(engine, "_dspSettings", settings);
            var first = BufferedSource(engine, [0.1, 0.1]);
            var source = new GaplessSource(first);
            Set(engine, "_session", first);
            Set(engine, "_gaplessSource", source);
            Set(engine, "_output", new CorrectionOutput("test-device"));
            engine.IsPlaying = true;
            try
            {
                engine.QueueNext(new(first.TimelineEpoch, 1, path));
                Require(Preloader(engine).Completion.Wait(5000), "Preparation timed out");
                var next = source.Pending!;
                var ring = typeof(Session).GetField("_pcmRing", Private)!.GetValue(next);
                engine.UpdateDsp(settings with { Balance = 0.2 }); // warm publication
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 100; i++)
                    engine.UpdateDsp(settings with { Balance = i % 2 == 0 ? 0.1 : 0.2, CompressorEnabled = true });
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                Console.WriteLine($"Gapless DSP edits: 100 updates, control-thread allocation={bytes} B, same pending session={ReferenceEquals(next, source.Pending)}");
                Require(ReferenceEquals(next, source.Pending) && ReferenceEquals(ring, typeof(Session).GetField("_pcmRing", Private)!.GetValue(next)),
                    "DSP edits replaced decoder/ring");
                Require(bytes < 256_000, "DSP edits allocated session-sized buffers");
                foreach (var session in new[] { first, next })
                {
                    var applied = (DspSettings)typeof(PcmEffects).GetField("_settings", Private)!.GetValue(session.Effects)!;
                    Require(applied.Balance == 0.2 && applied.CompressorEnabled, "Active/pending targets diverged");
                }
                engine.UpdateDeviceCorrections(new());
                engine.PreviewDsp(new(1, 0, "test-device", false, settings with { Balance = -0.3 }));
                Require(ReferenceEquals(next, source.Pending), "Correction/preview discarded pending decoder");
                var preview = (DspSettings)typeof(PcmEffects).GetField("_settings", Private)!.GetValue(next.Effects)!;
                Require(preview.Balance == -0.3, "Pending preview did not update");
                engine.PreviewDsp(new(2, 0, "", true, settings));
                Require(ReferenceEquals(next, source.Pending), "Ending preview discarded pending decoder");
            }
            finally
            {
                engine.StopGaplessAsync().GetAwaiter().GetResult();
                Invoke(engine, "DisposeSession");
            }
        });

        Run("Gapless: blocked open keeps only latest request, disposes late sessions and drains shutdown", () =>
        {
            var engine = Engine("WasapiShared");
            using var current = BufferedSource(engine, [0.1, 0.1]);
            var source = new GaplessSource(current);
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var opened = new List<long>();
            var cancelled = new List<Session>();
            Session? published = null;
            var gate = new object();
            var preloader = new GaplessPreloader(gate, (preparation, token) =>
            {
                opened.Add(preparation.Request.Token);
                if (preparation.Request.Token == 1)
                {
                    entered.Set();
                    if (!release.Wait(5000)) throw new TimeoutException("release blocked opener");
                }
                var session = BufferedSource(engine, [0.2, 0.2]);
                if (token.IsCancellationRequested) cancelled.Add(session);
                return session; // deliberately models a native open that ignored cancellation
            }, (_, session) => { published = session; return true; });
            void Submit(long id)
            {
                var request = new GaplessRequest(current.TimelineEpoch, id, path);
                preloader.SetPlan(request);
                preloader.Start(new(request, current, source, 88200, 0, 300, false, 1, false));
            }
            try
            {
                Submit(1);
                Require(entered.Wait(5000), "Opener did not run");
                for (long token = 2; token <= 100; token++) Submit(token);
                var allWork = preloader.Completion;
                release.Set();
                Require(allWork.Wait(5000), "Latest request failed to settle");
                Require(opened.SequenceEqual([1L, 100L]), "Superseded requests opened decoders");
                Require(cancelled.Count == 1 && (int)typeof(Session).GetField("_disposed", Private)!.GetValue(cancelled[0])! == 1,
                    "Late session was not disposed");
                Require(published != null && preloader.State == GaplessPreparationState.Ready, "Latest session did not publish");
                preloader.Retire(published!);
                published = null;
                entered.Reset();
                release.Reset();
                Submit(1);
                Require(entered.Wait(5000), "Second blocked open did not start");
                Submit(2);
                Task stopped = preloader.StopAsync();
                Require(!stopped.IsCompleted, "Stop abandoned the in-flight open");
                release.Set();
                Require(stopped.Wait(5000) && opened.SequenceEqual([1L, 100L, 1L]), "Shutdown opened queued work");
                Require(published == null && cancelled.All(s => (int)typeof(Session).GetField("_disposed", Private)!.GetValue(s)! == 1),
                    "Shutdown published or retained a late session");
            }
            finally
            {
                release.Set();
                preloader.StopAsync().GetAwaiter().GetResult();
                published?.Dispose();
            }
        });
    }

    private static void CheckStandbyFormatAllocation(string path)
    {
        var engine = Engine("WasapiShared");
        using var incompatible = Source(engine, RenderKind.Pcm, 44100);
        // Measure managed allocations on the opening thread only, after warming the native decoder.
        Measure(requireCompatible: false);
        Measure(requireCompatible: true);
        long unrestricted = Measure(requireCompatible: false);
        long rejected = Measure(requireCompatible: true);
        const int stereoRingBytes = 48000 * 2 * 2 * sizeof(double); // 48 kHz, stereo double, 2 s ring.
        Require(unrestricted - rejected >= stereoRingBytes, "Format rejection allocated a standby ring");
        Console.WriteLine($"Standby decode allocation: unrestricted={unrestricted} B, incompatible={rejected} B");

        long Measure(bool requireCompatible)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            using var session = Session.Open(engine, path, RenderKind.Pcm, 88200, 0, 1000,
                requiredPcmFormat: requireCompatible ? incompatible : null);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Require((session == null) == requireCompatible, "Fixture returned an unexpected decoder format");
            return allocated;
        }
    }

    private static void CheckTerminalStandbyRetirement()
    {
        foreach (bool failure in new[] { false, true })
        {
            var engine = Engine("WasapiShared");
            var gate = new object();
            Set(engine, "_streamLock", gate);
            var first = BufferedSource(engine, [0.3, 0.3]);
            var next = BufferedSource(engine, []);
            var source = new GaplessSource(first);
            Set(engine, "_session", first);
            Set(engine, "_gaplessSource", source);
            Set(engine, "_output", new CorrectionOutput("standby-lifecycle"));
            var preloader = new GaplessPreloader(gate, (_, _) => next,
                (preparation, session) => source.Queue(session, preparation.Request.Token));
            Set(engine, "_gaplessPreloader", preloader);
            engine.IsPlaying = true;
            try
            {
                var request = new GaplessRequest(first.TimelineEpoch, 42, Path.Combine(AppContext.BaseDirectory, "unused.wav"));
                preloader.SetPlan(request);
                preloader.Start(new(request, first, source, 88200, 0, 300, false, 1, false));
                Require(preloader.Completion.Wait(5000) && source.Pending == next, "Unused standby did not publish");
                if (failure) Set(first, "_decodeFailure", new IOException("Fixture decoder failed"));
                else source.FillPcm(new double[4], 2);
                Invoke(engine, "WatchdogTick");
                Require(!engine.IsPlaying && source.Current == first && source.CompletedToken == 0
                    && source.Pending == null && preloader.Request == null && next.DecodeCompletion.Wait(5000),
                    "Terminal playback retained a session it can no longer render");
            }
            finally
            {
                engine.StopGaplessAsync().GetAwaiter().GetResult();
                Invoke(engine, "DisposeSession");
            }
        }
    }

    private static void CheckProgressDoesNotWaitForRender()
    {
        var engine = Engine("WasapiShared");
        Set(engine, "_streamLock", new object());
        using var first = BufferedSource(engine, [0.3, 0.3]);
        var source = new GaplessSource(first);
        Set(engine, "_session", first);
        Set(engine, "_gaplessSource", source);
        var gate = typeof(GaplessSource).GetField("_gate", Private)!.GetValue(source)!;
        lock (gate)
        {
            var progress = Task.Run(() => engine.TryCaptureProgress(out var snapshot) && snapshot.Epoch == first.TimelineEpoch);
            Require(progress.Wait(2000) && progress.Result, "Unchanged session progress waited for the render gate");
        }
    }
}
