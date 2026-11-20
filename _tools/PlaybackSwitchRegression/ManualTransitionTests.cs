using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunManualTransitionTests()
    {
        Run("Manual next: exact overlap from current frame and short remaining cap", CheckManualTransitionWindows);
        Run("Manual next: an audible natural fade to the same token continues unchanged", CheckNaturalOverlapManualPoll);
        Run("Manual next: cold, waiting and ready album plans use Smart energy and actual handoff identity", CheckManualEngineHandoff);
        Run("Manual next: replacement, cancellation, invalid input and unavailable plans", CheckManualCancellation);
        Run("Manual next: render commit and cancellation publish one consistent identity", CheckManualCancelAtBoundary);
        Run("Manual next: standby underrun reports unavailable and retains the current track", CheckManualUnderrun);
    }

    private static void CheckManualTransitionWindows()
    {
        var engine = Engine("WasapiShared");
        foreach (int available in new[] { 6000, 100, 50 })
        {
            using var first = TransitionSession(engine, 1000, 8000, .75, -.25);
            using var next = TransitionSession(engine, 1000, 8000, -.25, .75);
            var source = new GaplessSource(first);
            var output = new double[16000];
            int playedBefore = 8000 - available;
            source.FillPcm(output, playedBefore);
            Require(source.Queue(next, 31, TrackTransitionStyle.Crossfade, 1), "Queue rejected");
            var status = source.RequestTransitionNow(first.TimelineEpoch, 31, TrackTransitionStyle.Crossfade, 1);
            if (available == 50)
            {
                Require(status == TransitionNowStatus.Unavailable, "Less than the minimum overlap was accepted");
                source.FillPcm(output, 80);
                CheckStereo(output, 49, .75, -.25);
                Require(source.Current == first && next.FramesPlayed == 0, "Unavailable manual transition consumed the pending track");
                continue;
            }

            Require(status == TransitionNowStatus.Preparing && next.FramesPlayed == 0, "Manual acceptance reported render completion");
            int overlap = Math.Min(1000, available);
            source.FillPcm(output, overlap);
            CheckStereo(output, 0, .75 - 1.0 / overlap, -.25 + 1.0 / overlap);
            CheckStereo(output, overlap - 1, -.25, .75);
            Require(first.FramesPlayed == playedBefore + overlap && next.FramesPlayed == overlap
                && source.CompletedToken == 31 && source.Current == next, "Manual fade sought to EOF or replayed incoming frames");
        }
    }

    private static void CheckNaturalOverlapManualPoll()
    {
        var engine = Engine("WasapiShared");
        using var first = TransitionSession(engine, 1000, 3000, .6, -.4);
        using var next = TransitionSession(engine, 1000, 3000, -.2, .8);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 32, TrackTransitionStyle.Crossfade, 1), "Queue rejected");
        var output = new double[4400];
        source.FillPcm(output, 2200);
        Require(next.FramesPlayed == 200, "Natural overlap never started");
        CheckStereo(output, 2199, .6 * .8 - .2 * .2, -.4 * .8 + .8 * .2);
        Require(source.RequestTransitionNow(first.TimelineEpoch, 32, TrackTransitionStyle.Crossfade, 3) == TransitionNowStatus.Started,
            "Manual poll did not recognize audible natural overlap");
        source.FillPcm(output, 800);
        CheckStereo(output, 0, .6 * .799 - .2 * .201, -.4 * .799 + .8 * .201);
        CheckStereo(output, 799, -.2, .8);
        Require(source.CompletedToken == 32 && next.FramesPlayed == 1000, "Manual poll restarted the natural fade");
    }

    private static void CheckManualEngineHandoff()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "manual-transition-intro.wav");
        WriteLoudnessTone(path, 0.2f, frequency: 4000);
        foreach (var existingState in new[] { GaplessPreparationState.Empty, GaplessPreparationState.Waiting, GaplessPreparationState.Ready })
        {
            var (engine, source, first) = OpenManualFixture();
            try
            {
                if (existingState == GaplessPreparationState.Ready) Set(first, "<TotalMs>k__BackingField", 6000L);
                var request = new GaplessRequest(first.TimelineEpoch, 33, path);
                GaplessRequest? existingPlan = null;
                Session? prepared = null;
                var output = new double[480 * 2];
                for (int i = 0; i < 20; i++) source.FillPcm(output, 480);
                if (existingState != GaplessPreparationState.Empty)
                {
                    existingPlan = request with { PreserveAlbumBoundary = true };
                    Require(engine.TryQueueNext(existingPlan), "Album plan rejected");
                    if (existingState == GaplessPreparationState.Ready) prepared = WaitManualReady(engine, source);
                    Require(Preloader(engine).State == existingState, "Fixture entered the wrong preload state");
                }
                Require(engine.TryTransitionNow(request) == TransitionNowStatus.Preparing, "Manual transition did not reserve the correct local plan");
                var next = WaitManualReady(engine, source);
                Require(existingPlan == null || ReferenceEquals(Preloader(engine).Request, existingPlan),
                    "Manual request replaced an identical natural plan");
                Require(prepared == null || next == prepared, "Manual Smart reopened a prepared album successor");
                Require(Math.Abs(next.IntroMeanSquare - .02) < 1e-8, "Manual Smart did not measure the album intro");
                Require(engine.TryTransitionNow(request) == TransitionNowStatus.Preparing && next.FramesPlayed == 0,
                    "Ready decoder reported rendered frames before the callback");
                source.FillPcm(output, 480);
                CheckStereo(output, 0, .3 * (1 - 1.0 / 24000), .3 * (1 - 1.0 / 24000));
                Require(engine.TryTransitionNow(request) == TransitionNowStatus.Started, "Started poll did not report audible overlap");
                for (int i = 1; i < 50; i++) source.FillPcm(output, 480);
                Require(engine.TryTransitionNow(request) == TransitionNowStatus.Committed && first.FramesPlayed == 33600,
                    "Manual Smart did not commit after half a second at the original playhead");
                Require(engine.TryCaptureProgress(out var progress) && progress.GaplessToken == request.Token
                    && progress.Epoch == next.TimelineEpoch && progress.CurrentMs == 500,
                    "Committed handoff lost track position or identity");
                Require(engine.TryTransitionNow(request with { Path = path + ".different" }) == TransitionNowStatus.Unavailable
                    && engine.TryTransitionNow(request with { Epoch = request.Epoch + 1000 }) == TransitionNowStatus.Unavailable
                    && engine.TryTransitionNow(request) == TransitionNowStatus.Committed,
                    "Committed identity matched a forged path or epoch, or did not survive a duplicate poll");
            }
            finally { CloseManualFixture(engine); }
        }
    }

    private static void CheckManualCancellation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "manual-transition-context.wav");
        string otherPath = Path.Combine(AppContext.BaseDirectory, "manual-transition-other.wav");
        WriteLoudnessTone(path, 0.1f);
        WriteLoudnessTone(otherPath, 0.15f);
        var (engine, source, first) = OpenManualFixture(TrackTransitionStyle.Crossfade);
        try
        {
            var initial = new GaplessRequest(first.TimelineEpoch, 34, path);
            var other = initial with { Token = 35, Path = otherPath };
            Require(engine.TryTransitionNow(initial with { Epoch = initial.Epoch + 1 }) == TransitionNowStatus.Unavailable
                && engine.TryTransitionNow(initial with { Path = "relative.wav" }) == TransitionNowStatus.Unavailable,
                "Manual request accepted an invalid epoch or a relative path");
            engine.UpdateDsp(new DspSettings { TransitionStyle = TrackTransitionStyle.Gapless });
            Require(engine.TryTransitionNow(initial) == TransitionNowStatus.Unavailable, "Gapless-only mode accepted manual overlap");
            engine.UpdateDsp(new DspSettings { TransitionStyle = TrackTransitionStyle.Crossfade, CrossfadeSeconds = 1 });
            Require(engine.TryTransitionNow(initial) == TransitionNowStatus.Preparing, "Initial attempt not queued");
            WaitManualReady(engine, source);
            var output = new double[960];
            source.FillPcm(output, 480);
            Require(engine.TryTransitionNow(initial) == TransitionNowStatus.Started, "Fixture overlap did not start");
            Require(!engine.TryQueueNext(other), "Advisory QueueNext displaced an active manual request");
            Require(engine.TryTransitionNow(other) == TransitionNowStatus.Preparing, "New manual next could not replace active overlap");
            Require(engine.TryTransitionNow(initial) == TransitionNowStatus.Unavailable, "Late poll resurrected the older manual target");
            WaitManualReady(engine, source);
            Require(engine.TryQueueNext(new(0, 0, "")) && source.Pending == null, "Cancellation did not detach the new pending session");
            Require(engine.TryTransitionNow(other) == TransitionNowStatus.Unavailable && engine.TryTransitionNow(initial) == TransitionNowStatus.Unavailable,
                "Duplicate poll resurrected a cancelled or superseded plan");
            var missing = other with { Token = 36, Path = otherPath + ".missing" };
            Require(engine.TryTransitionNow(missing) == TransitionNowStatus.Preparing && Preloader(engine).Completion.Wait(5000),
                "Missing-file preparation did not finish");
            Require(engine.TryTransitionNow(missing) == TransitionNowStatus.Unavailable
                && engine.TryTransitionNow(missing) == TransitionNowStatus.Unavailable && source.CompletedToken == 0,
                "Failed plan was re-opened or reported an unrendered handoff");

            var seeking = initial with { Token = 37 };
            Require(engine.TryTransitionNow(seeking) == TransitionNowStatus.Preparing, "Pre-seek plan not queued");
            WaitManualReady(engine, source);
            source.FillPcm(output, 480);
            engine.ChangeWaveChannelTime(1000);
            Require(source.Pending == null && first.TimelineEpoch != seeking.Epoch
                && engine.TryTransitionNow(seeking) == TransitionNowStatus.Unavailable, "Seek retained a stale manual transition");
        }
        finally { CloseManualFixture(engine); }
    }

    private static void CheckManualCancelAtBoundary()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "manual-transition-boundary.wav");
        WriteLoudnessTone(path, .2f);
        var (engine, source, first) = OpenManualFixture();
        try
        {
            var request = new GaplessRequest(first.TimelineEpoch, 38, path);
            Require(engine.TryTransitionNow(request) == TransitionNowStatus.Preparing, "Boundary plan rejected");
            var next = WaitManualReady(engine, source);
            var output = new double[24000 * 2];
            source.FillPcm(output, 480);
            Require(engine.TryTransitionNow(request) == TransitionNowStatus.Started, "Boundary fade never started");

            using var start = new ManualResetEventSlim();
            var render = Task.Run(() => { start.Wait(); source.FillPcm(output, 23520); });
            var cancel = Task.Run(() => { start.Wait(); return engine.TryQueueNext(new(0, 0, "")); });
            start.Set();
            Require(Task.WaitAll([render, cancel], 5000) && cancel.Result && source.Pending == null, "Render and cancellation did not settle");
            var status = engine.TryTransitionNow(request);
            var identity = engine.GetGaplessIdentity();
            if (source.Current == next)
                Require(status == TransitionNowStatus.Committed && identity == (request.Token, next.TimelineEpoch),
                    "Cancellation discarded a committed handoff");
            else
                Require(source.Current == first && status == TransitionNowStatus.Unavailable && identity == (0, first.TimelineEpoch),
                    "Cancellation published a token for a handoff that never rendered");
        }
        finally { CloseManualFixture(engine); }
    }

    private static void CheckManualUnderrun()
    {
        var engine = Engine("WasapiShared");
        using var first = TransitionSession(engine, 1000, 4000, .6, .6);
        using var next = TransitionSession(engine, 1000, 300, .2, .2, ended: false);
        Set(next, "<TotalMs>k__BackingField", 4000L);
        var source = new GaplessSource(first);
        Require(source.Queue(next, 37, TrackTransitionStyle.Crossfade, 1, transitionNow: true), "Underrun fixture rejected");
        var output = new double[800];
        source.FillPcm(output, 300);
        Require(source.RequestTransitionNow(first.TimelineEpoch, 37, TrackTransitionStyle.Crossfade, 1) == TransitionNowStatus.Started,
            "Prepared decoder did not start before underrun");
        source.FillPcm(output, 400);
        CheckStereo(output, 0, .6 * .715 + .2 * .285, .6 * .715 + .2 * .285);
        CheckStereo(output, 19, .6, .6);
        Require(source.RequestTransitionNow(first.TimelineEpoch, 37, TrackTransitionStyle.Crossfade, 1) == TransitionNowStatus.Unavailable
            && source.Current == first && next.FramesPlayed == 300 && source.CompletedToken == 0,
            "Underrun re-armed or committed an incomplete transition");
    }

    private static (PlaybackEngine Engine, GaplessSource Source, Session First) OpenManualFixture(TrackTransitionStyle style = TrackTransitionStyle.Smart)
    {
        var engine = Engine("WasapiShared");
        Set(engine, "_streamLock", new object());
        Set(engine, "_dspSettings", new DspSettings { TransitionStyle = style, CrossfadeSeconds = 1 });
        var first = TransitionSession(engine, 48000, 192000, .3, .3, ended: false);
        Set(first, "<TotalMs>k__BackingField", 30000L);
        var source = new GaplessSource(first);
        Set(engine, "_session", first);
        Set(engine, "_gaplessSource", source);
        Set(engine, "_output", new CorrectionOutput("manual-test"));
        engine.IsPlaying = true;
        return (engine, source, first);
    }

    private static Session WaitManualReady(PlaybackEngine engine, GaplessSource source)
    {
        Require(Preloader(engine).Completion.Wait(5000), "Preloader did not finish");
        Require(SpinWait.SpinUntil(() => source.Pending is { ReadyFrames: >= 28800 }, 5000), "Pending decoder did not fill its ring");
        return source.Pending!;
    }

    private static void CloseManualFixture(PlaybackEngine engine)
    {
        engine.StopGaplessAsync().GetAwaiter().GetResult();
        Invoke(engine, "DisposeSession");
    }
}
