using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunTrackTransitionTests()
    {
        Run("Crossfade: aligned dual PCM lanes, exact boundary, sequential handoffs and no callback allocations", CheckFixedCrossfade);
        Run("Crossfade: streams both rings through a fade larger than either ring", CheckStreamingCrossfade);
        Run("Crossfade: independent normalization, one continuous limiter and shared master volume", CheckCrossfadeDsp);
        Run("Smart transition: incoming energy and shortest track constrain the overlap", CheckSmartCrossfade);
        Run("Crossfade: waits for the outgoing prebuffer without consuming the incoming ring", CheckCrossfadePrebuffer);
        Run("Crossfade: unknown length, inaccurate duration, underrun and cancellation preserve outgoing audio", CheckCrossfadeFallback);
        Run("Crossfade: cancellation and underrun release the last incoming contribution over at most 20 ms", CheckCrossfadeWithdrawal);
        Run("Smart transition: engine honors album continuity and reports consumed incoming position", CheckEngineTrackTransitions);
        Run("Crossfade progress: outgoing commit stays stable and incoming uses its own rate with shared device/DSP delay", CheckIncomingTimelineRate);
        RunManualTransitionTests();
    }

    private static void CheckFixedCrossfade()
    {
        var engine = Engine("WasapiShared");
        using var first = TransitionSession(engine, 1000, 3000, 0.75, -0.25);
        using var next = TransitionSession(engine, 1000, 3000, -0.25, 0.75);
        using var third = TransitionSession(engine, 1000, 3000, 0.125, 0.375);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 20, TrackTransitionStyle.Crossfade, 1), "Initial plan rejected");
        var output = new double[3000 * 2];
        source.FillPcm(output, 1999);
        Require(next.FramesPlayed == 0 && source.CompletedToken == 0, "Incoming consumed before overlap");
        source.FillPcm(output.AsSpan(1999 * 2), 1001);
        CheckStereo(output, 1999, .75, -.25);
        CheckStereo(output, 2000, .749, -.249);
        CheckStereo(output, 2499, .25, .25);
        CheckStereo(output, 2999, -.25, .75);
        Require(source.Current == next && source.CompletedToken == 20 && next.FramesPlayed == 1000
            && next.CurrentMs == 1000 && source.SubmittedFrames == 3000, "Wrong timing or identity at exact boundary");

        Require(source.Queue(third, 21, TrackTransitionStyle.Crossfade, 1), "Second plan rejected");
        source.FillPcm(output, 1100);
        long before = GC.GetAllocatedBytesForCurrentThread();
        source.FillPcm(output, 1000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(allocated == 0, $"Mix callback allocated {allocated} bytes");
        CheckStereo(output, 899, .125, .375);
        CheckStereo(output, 999, .125, .375);
        Require(source.Current == third && source.CompletedToken == 21 && third.FramesPlayed == 1100
            && source.SubmittedFrames == 5100, "Second handoff replayed overlap or counted media frames twice");
    }

    private static void CheckStreamingCrossfade()
    {
        var engine = Engine("WasapiShared");
        const int length = 5000, rate = 1000, capacity = 300, block = 50;
        using var first = TransitionSession(engine, rate, length, 0, 0, ended: false, capacity: capacity);
        using var next = TransitionSession(engine, rate, length, 0, 0, ended: false, capacity: capacity);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 22, TrackTransitionStyle.Crossfade, 2), "Stream plan rejected");
        double[] outgoing = StereoFrames(capacity, 0.6, -0.4);
        double[] incoming = StereoFrames(capacity, -0.2, 0.8);
        double[] output = new double[block * 2];
        int producedOut = 0, producedIn = 0;
        for (int start = 0; start < length + block; start += block)
        {
            Supply(first, outgoing, ref producedOut);
            Supply(next, incoming, ref producedIn);
            source.FillPcm(output, block);
            for (int frame = 0; frame < block; frame++)
            {
                double mix = Math.Clamp((start + frame - (length - 2000) + 1) / 2000.0, 0, 1);
                CheckStereo(output, frame, .6 * (1 - mix) - .2 * mix, -.4 * (1 - mix) + .8 * mix);
            }
        }
        Require(next.FramesPlayed == 2050 && source.CompletedToken == 22, "Streaming crossfade lost the decoded intro position");

        void Supply(Session session, double[] samples, ref int produced)
        {
            var ring = TransitionRing(session);
            int take = Math.Min(length - produced, capacity - session.ReadyFrames);
            Require(ring.Push(samples, take, static () => false), "Fixture push failed");
            produced += take;
            if (produced == length) ring.MarkInputEnded();
        }
    }

    private static void CheckCrossfadeDsp()
    {
        const int rate = 48000, length = rate * 2, overlap = rate;
        var engine = Engine("WasapiShared");
        var settings = new DspSettings { NormalizeLoudness = true, TargetLufs = -18, TruePeakLimiterEnabled = true, TruePeakCeilingDb = -3 };
        using var first = TransitionSession(engine, rate, length, 1, -1);
        using var next = TransitionSession(engine, rate, length, -1, 1);
        Set(first.Effects!, "_measurement", new LoudnessMeasurement(-18, .1));
        Set(next.Effects!, "_measurement", new LoudnessMeasurement(-24, .1));
        first.ConfigureDsp(settings);
        next.ConfigureDsp(settings);
        first.Gain!.SetImmediately(.9);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 23, TrackTransitionStyle.Crossfade, 1), "DSP plan rejected");
        double normalizedIncoming = Math.Pow(10, .3);
        var expectedPcm = StereoFrames(length * 2 - overlap, 0, 0);
        for (int frame = 0; frame < expectedPcm.Length / 2; frame++)
        {
            double mix = Math.Clamp((frame - overlap + 1.0) / overlap, 0, 1);
            expectedPcm[frame * 2] = (1 - mix) - normalizedIncoming * mix;
            expectedPcm[frame * 2 + 1] = -expectedPcm[frame * 2];
        }
        using var continuous = TransitionSession(engine, rate, expectedPcm);
        continuous.ConfigureDsp(settings with { NormalizeLoudness = false });
        continuous.Gain!.SetImmediately(.9);
        var actual = new double[expectedPcm.Length + 2048];
        var expected = new double[actual.Length];
        int rampFrame = length - rate / 100;
        source.FillPcm(actual, rampFrame);
        continuous.FillPcm(expected, rampFrame);
        first.Gain.RampTo(.6, 20);
        continuous.Gain.RampTo(.6, 20);
        source.FillPcm(actual.AsSpan(rampFrame * 2), actual.Length / 2 - rampFrame);
        continuous.FillPcm(expected.AsSpan(rampFrame * 2), expected.Length / 2 - rampFrame);
        for (int sample = 0; sample < actual.Length; sample++)
            Require(Math.Abs(actual[sample] - expected[sample]) < 1e-10, $"DSP history or track gain changed at sample {sample}");
        Require(source.SubmittedFrames == continuous.SubmittedFrames, "Limiter delay was repeated or dropped");
    }

    private static void CheckSmartCrossfade()
    {
        var engine = Engine("WasapiShared");
        foreach ((double energy, int length, int expected) in new[] { (0.0001, 12000, 3000), (.04, 12000, 1500), (0.0001, 600, 150) })
        {
            using var first = TransitionSession(engine, 1000, 12000, 0.9, 0.9);
            using var next = TransitionSession(engine, 1000, length, 0.1, 0.1);
            Set(next, "_introMeanSquare", energy);
            var source = new GaplessSource(first);
            Require(source.Queue(next, 24, TrackTransitionStyle.Smart, 3), "Smart plan rejected");
            double[] output = new double[24000];
            source.FillPcm(output, 12000);
            CheckStereo(output, 12000 - expected - 1, .9, .9);
            CheckStereo(output, 12000 - expected, .9 - .8 / expected, .9 - .8 / expected);
            Require(source.Current == next && next.FramesPlayed == expected, "Wrong adaptive overlap length");
        }
    }

    private static void CheckCrossfadeFallback()
    {
        var engine = Engine("WasapiShared");
        using (var first = TransitionSession(engine, 1000, 400, .6, .6, ended: false))
        using (var next = TransitionSession(engine, 1000, 500, .2, .2))
        {
            Set(first, "<TotalMs>k__BackingField", 0L);
            var source = new GaplessSource(first);
            Require(source.Queue(next, 25, TrackTransitionStyle.Crossfade, 1), "Unknown-duration plan rejected");
            var output = new double[900];
            source.FillPcm(output, 400);
            Require(next.FramesPlayed == 0, "Estimated an overlap without duration or decoded EOF");
            TransitionRing(first).MarkInputEnded();
            source.FillPcm(output, 50);
            CheckStereo(output, 0, .2, .2);
            Require(source.CompletedToken == 25, "Unknown duration lost gapless fallback");
        }

        foreach (string reason in new[] { "inaccurate duration", "incoming underrun", "cancel" })
        {
            using var first = TransitionSession(engine, 1000, 2000, .6, .6, ended: false);
            using var next = TransitionSession(engine, 1000, 300, .2, .2, ended: false);
            Set(first, "<TotalMs>k__BackingField", 2000L);
            Set(next, "<TotalMs>k__BackingField", 2000L);
            var source = new GaplessSource(first);
            Require(source.Queue(next, 26, TrackTransitionStyle.Crossfade, 1), "Recovery plan rejected");
            double[] output = new double[4000];
            source.FillPcm(output, 1100);
            Require(next.FramesPlayed == 100, "Fixture never entered overlap");
            if (reason == "inaccurate duration")
            {
                Require(TransitionRing(first).Push(StereoFrames(100, .6, .6), 100, static () => false), "Fixture push failed");
                TransitionRing(first).MarkInputEnded();
            }
            else if (reason == "cancel") Require(source.Cancel() == next, "Cancel did not return the standby session");
            source.FillPcm(output, 1000);
            CheckStereo(output, 300, .6, .6);
            Require(source.Current == first && source.CompletedToken == 0, $"{reason} published the wrong identity");
            Require(first.FramesPlayed == (reason == "inaccurate duration" ? 2100 : 2000), $"{reason} lost outgoing material");
        }
    }

    private static void CheckCrossfadePrebuffer()
    {
        var engine = Engine("WasapiShared");
        using var first = TransitionSession(engine, 1000, StereoFrames(250, .6, .6), ended: false,
            capacity: 400, frames: 400, prebuffer: 300);
        using var next = TransitionSession(engine, 1000, 500, .2, .2);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 28, TrackTransitionStyle.Crossfade, 1), "Prebuffer plan rejected");
        var output = new double[800];
        source.FillPcm(output, 300);
        CheckStereo(output, 299, 0, 0);
        Require(source.SubmittedFrames == 0 && first.FramesPlayed == 0 && next.FramesPlayed == 0,
            "Crossfade started before the outgoing PCM watermark opened");

        var ring = TransitionRing(first);
        Require(ring.Push(StereoFrames(150, .6, .6), 150, static () => false), "Fixture push failed");
        ring.MarkInputEnded();
        source.FillPcm(output, 400);
        CheckStereo(output, 199, .6, .6);
        CheckStereo(output, 200, .598, .598);
        CheckStereo(output, 399, .2, .2);
        Require(source.CompletedToken == 28 && source.SubmittedFrames == 400 && next.FramesPlayed == 200,
            "Prebuffer did not resume its scheduled overlap");
    }

    private static void CheckCrossfadeWithdrawal()
    {
        var engine = Engine("WasapiShared");
        foreach ((bool cancel, bool outgoingStarved) in new[] { (true, false), (false, false), (false, true) })
        {
            double outgoingRight = outgoingStarved ? 0 : .2;
            using var first = TransitionSession(engine, 1000, outgoingStarved ? 1800 : 2000, 0, outgoingRight, ended: !outgoingStarved);
            using var next = TransitionSession(engine, 1000, 800, .8, -.4, ended: false);
            Set(first, "<TotalMs>k__BackingField", 2000L);
            Set(next, "<TotalMs>k__BackingField", 2000L);
            var source = new GaplessSource(first);
            Require(source.Queue(next, 29, TrackTransitionStyle.Crossfade, 1), "Withdrawal plan rejected");
            var output = new double[3600];
            source.FillPcm(output, 1800);
            CheckStereo(output, 1799, .64, outgoingRight * .2 - .32);
            if (cancel)
            {
                Require(source.Cancel() == next, "Explicit cancellation did not return the standby session");
                next.Dispose();
            }
            source.FillPcm(output, 7);
            source.FillPcm(output.AsSpan(14), 23);
            for (int frame = 0; frame < 30; frame++)
            {
                double weight = .8 * Math.Max(0, 19 - frame) / 20;
                CheckStereo(output, frame, .8 * weight, outgoingRight * (1 - weight) - .4 * weight);
            }
            Require(next.FramesPlayed == 800 && source.Current == first && source.CompletedToken == 0,
                "Withdrawing the mix consumed more incoming audio or committed the wrong identity");
            Require(source.SubmittedFrames == (outgoingStarved ? 1820 : 1830), "Submitted frame count lost the recovery audio");
        }
    }

    private static void CheckEngineTrackTransitions()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "track-transition-intro.wav");
        WriteLoudnessTone(path, 0.2f, frequency: 4000); // Exact multiples of 4 kHz expose stride aliasing.
        foreach (bool preserveAlbum in new[] { true, false })
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            var settings = new DspSettings { TransitionStyle = TrackTransitionStyle.Smart, CrossfadeSeconds = 1 };
            Set(engine, "_dspSettings", preserveAlbum ? settings with { TransitionStyle = TrackTransitionStyle.Gapless } : settings);
            var first = TransitionSession(engine, 48000, 192000, .3, .3);
            var source = new GaplessSource(first);
            Set(engine, "_session", first);
            Set(engine, "_gaplessSource", source);
            Set(engine, "_output", new CorrectionOutput("natural-transition"));
            engine.IsPlaying = true;
            try
            {
                var request = new GaplessRequest(first.TimelineEpoch, 27, path, preserveAlbum);
                Set(engine, "_fadeBusy", 1);
                Require(!engine.TryQueueNext(request), "Manual switch queued PCM using a stale outgoing epoch");
                Set(engine, "_fadeBusy", 0);
                Require(engine.TryQueueNext(request), "Smart engine plan rejected");
                Require(Preloader(engine).Completion.Wait(5000), "Standby decoder did not publish");
                Require(SpinWait.SpinUntil(() => source.Pending is { ReadyFrames: >= 28800 }, 5000), "Standby audio not ready");
                var next = source.Pending!;
                if (preserveAlbum)
                {
                    // Settings can reach the engine before the coordinator refreshes its existing queue plan.
                    engine.UpdateDsp(settings with { TransitionStyle = TrackTransitionStyle.Crossfade, CrossfadeSeconds = 2 });
                    engine.UpdateDsp(settings);
                    Require(source.Pending == next && Preloader(engine).Request == request,
                        "Changing transition settings detached a still-valid album plan");
                }
                Require(preserveAlbum || Math.Abs(next.IntroMeanSquare - .02) < 1e-8,
                    $"Real decoder mismeasured the 4 kHz intro: {next.IntroMeanSquare}");
                double[] output = new double[2 * 480];
                Require(engine.TryCaptureProgress(out var before) && before.TransitionToken == 0, "Prepared silent track was presented as mixing");
                for (int offset = 0; offset < 192000; offset += 480)
                {
                    source.FillPcm(output, 480);
                    if (preserveAlbum) CheckStereo(output, 0, .3, .3);
                    if (offset == 180000)
                    {
                        Require(engine.TryCaptureProgress(out var during) && during.Epoch == first.TimelineEpoch && during.GaplessToken == 0,
                            "Natural overlap changed the committed track before handoff");
                        Require(preserveAlbum ? during.TransitionToken == 0 : during.TransitionToken == 27 && during.TransitionEpoch == next.TimelineEpoch
                            && during.TransitionCurrentMs == 260 && during.TransitionTotalMs == next.TotalMs,
                            "Natural overlap did not expose its advancing incoming position or previewed a gapless splice");
                    }
                }
                if (preserveAlbum) source.FillPcm(output, 480);
                Require(engine.TryCaptureProgress(out var progress) && progress.GaplessToken == 27
                    && progress.Epoch == next.TimelineEpoch && progress.CurrentMs == (preserveAlbum ? 10 : 500) && progress.TransitionToken == 0,
                    $"Engine reported incorrect incoming progress: {progress.CurrentMs}");
                Require(engine.TryTransitionNow(request) == TransitionNowStatus.Committed,
                    "Manual poll missed an already committed natural handoff");
            }
            finally
            {
                engine.StopGaplessAsync().GetAwaiter().GetResult();
                Invoke(engine, "DisposeSession");
            }
        }
    }

    private static void CheckIncomingTimelineRate()
    {
        const int rate = 48000;
        const long pendingAudioMs = 40;
        var engine = Engine("WasapiShared");
        Set(engine, "_streamLock", new object());
        using var first = TransitionSession(engine, rate, rate * 8, .2, .2);
        using var next = TransitionSession(engine, rate, rate * 4, .4, .4);
        Set(next, "<PlaybackRate>k__BackingField", 2d);
        first.ConfigureDsp(new() { TruePeakLimiterEnabled = true });
        var source = new GaplessSource(first);
        Set(engine, "_session", first);
        Set(engine, "_gaplessSource", source);
        Set(engine, "_output", new CorrectionOutput("preview-delay", pendingAudioMs));
        engine.IsPlaying = true;
        Require(source.Queue(next, 42, TrackTransitionStyle.Crossfade, 1, transitionNow: true), "Queue rejected");
        var output = new double[rate / 5 * 2];
        source.FillPcm(output, rate / 50);
        Require(engine.TryCaptureProgress(out var initially) && initially.TransitionToken == 42 && initially.TransitionCurrentMs == 0,
            "Incoming timeline went negative behind the pipeline");
        source.FillPcm(output, rate / 10);
        long expected = next.CurrentMs - (pendingAudioMs + first.DspDelayMs) * 2;
        Require(first.DspDelayMs > 0 && engine.TryCaptureProgress(out var later) && later.Epoch == first.TimelineEpoch && later.GaplessToken == 0
            && later.TransitionEpoch == next.TimelineEpoch && later.TransitionCurrentMs == expected && later.TransitionPlaybackRate == 2,
            "Incoming clock did not apply the actual shared pipeline and incoming playback rate");
        Require(source.Cancel() == next && engine.TryCaptureProgress(out var cleared) && cleared.TransitionToken == 0
            && cleared.Epoch == first.TimelineEpoch, "Cancellation did not restore the outgoing clock");
    }

    private static Session TransitionSession(PlaybackEngine engine, int rate, int frames, double left, double right,
        bool ended = true, int capacity = 0) => TransitionSession(engine, rate, StereoFrames(capacity == 0 ? frames : 0, left, right),
            ended, capacity, frames);

    private static Session TransitionSession(PlaybackEngine engine, int rate, double[] samples, bool ended = true,
        int capacity = 0, int frames = 0, int prebuffer = 0)
    {
        var session = Source(engine, RenderKind.Pcm, rate);
        int buffered = samples.Length / 2;
        int length = frames == 0 ? buffered : frames;
        Set(session, "<TotalMs>k__BackingField", (long)length * 1000 / rate);
        var ring = new PcmRing(2, capacity == 0 ? Math.Max(1, buffered + 100) : capacity, prebuffer, -1);
        Set(session, "_pcmRing", ring);
        Require(ring.Push(samples, buffered, static () => false), "Fixture push failed");
        if (ended) ring.MarkInputEnded();
        return session;
    }

    private static PcmRing TransitionRing(Session session) =>
        (PcmRing)typeof(Session).GetField("_pcmRing", Private)!.GetValue(session)!;

    private static double[] StereoFrames(int frames, double left, double right)
    {
        double[] samples = new double[frames * 2];
        for (int frame = 0; frame < frames; frame++)
        {
            samples[frame * 2] = left;
            samples[frame * 2 + 1] = right;
        }
        return samples;
    }

    private static void CheckStereo(double[] output, int frame, double left, double right)
    {
        double observedLeft = output[frame * 2], observedRight = output[frame * 2 + 1];
        Require(Math.Abs(observedLeft - left) < 1e-11 && Math.Abs(observedRight - right) < 1e-11,
            $"Frame {frame} got ({observedLeft}, {observedRight}), expected ({left}, {right})");
    }
}
