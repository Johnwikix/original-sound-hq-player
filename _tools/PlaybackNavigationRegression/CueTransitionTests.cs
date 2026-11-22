using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

internal static partial class Regression
{
    private static async Task CheckCueTransitionsAsync()
    {
        var saved = AppSettings.Dsp;
        string firstImage = Path.GetFullPath("first-image.flac");
        string secondImage = Path.GetFullPath("second-image.flac");
        var cue = CreateCue(401, firstImage, 1, 0);
        var contiguous = CreateCue(402, firstImage, 2, 44100);
        var skipped = CreateCue(403, firstImage, 3, 88200);
        var otherImage = CreateCue(404, secondImage, 1, 44100);
        var whole = new Music(405) { Path = Path.GetFullPath("whole.wav"), Duration = TimeSpan.FromSeconds(5) };
        var albumWhole = new Music(406)
        {
            Path = Path.GetFullPath("album-whole.wav"), Album = "Common album", Author = "Common artist",
            TrackNumber = 2, DiskNumber = 1, Duration = TimeSpan.FromSeconds(5),
        };
        try
        {
            AppSettings.Dsp = saved with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade };
            await CheckHostHandoffAsync(cue, contiguous, preserveBoundary: true, manual: false, initialMs: 0);
            await CheckHostHandoffAsync(cue, contiguous, preserveBoundary: true, manual: true, initialMs: 300);
            await CheckHostHandoffAsync(cue, skipped, preserveBoundary: false, manual: false, initialMs: 300);
            await CheckHostHandoffAsync(cue, otherImage, preserveBoundary: false, manual: false, initialMs: 300);
            await CheckHostHandoffAsync(cue, whole, preserveBoundary: false, manual: false, initialMs: 300);
            await CheckHostHandoffAsync(whole, cue, preserveBoundary: false, manual: true, initialMs: 300);
            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Smart };
            await CheckHostHandoffAsync(cue, albumWhole, preserveBoundary: true, manual: false, initialMs: 0);

            await CheckCueToWholeClockAsync(cue, whole, manual: false);
            await CheckCueToWholeClockAsync(cue, whole, manual: true);
            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Gapless };
            await CheckCueToWholeClockAsync(cue, whole, manual: true);
            AppSettings.Dsp = AppSettings.Dsp with { GaplessPlayback = false };
            await CheckCueToWholeClockAsync(cue, whole, manual: true);
        }
        finally { AppSettings.Dsp = saved; }

        static Music CreateCue(int id, string path, int track, long start) => new(id)
        {
            Path = path, CuePath = Path.ChangeExtension(path, "cue"), IsCueTrack = true, CueTrackNo = track,
            TrackNumber = track, DiskNumber = 1, Album = "Common album", Author = "Common artist",
            SampleRate = 44100, StartSample = start, EndSample = start + 44100, Duration = TimeSpan.FromSeconds(1),
        };
    }

    private static async Task CheckHostHandoffAsync(Music from, Music next, bool preserveBoundary, bool manual, long initialMs)
    {
        using var test = new Scenario();
        long fromId = await StartCueTransitionAsync(test, from, next);
        await WaitAsync(() => test.Ipc.Queued.Any(request => request.Token > 0));
        var plan = test.Ipc.Queued.Last(request => request.Token > 0);
        string label = $"{from.Id}->{next.Id} {(manual ? "manual Next" : "natural")}";
        AudioSampleRange? targetRange = next.IsCueTrack ? new(next.StartSample, next.EndSample, next.SampleRate) : null;
        Check(plan.Path == next.Path && plan.SampleRange == targetRange && plan.PreserveAlbumBoundary == preserveBoundary,
            $"CUE {label}: host planned the correct target, sample range and natural-boundary preference");
        Task? nextTask = null;
        if (manual)
        {
            test.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
            nextTask = test.Coordinator.PlayNextAsync();
            await WaitAsync(() => test.Ipc.Manual.Count == 1);
            Check(test.Ipc.Manual[0] == plan, $"CUE {label}: manual Next promotes the queued decoder using its exact identity");
        }

        long reportedPosition = -1;
        test.Coordinator.TrackStarted += (music, position, _) =>
        {
            if (music == next) reportedPosition = position;
        };
        test.Ipc.Progress = new(3, 82, initialMs, (long)next.Duration.TotalMilliseconds, 0, true,
            GaplessToken: plan.Token, PlaybackId: next.IsCueTrack ? plan.Token : 0);
        test.Ipc.Transition(plan.Token, 82, initialMs);
        if (nextTask is not null) await nextTask;
        await WaitAsync(() => test.State.CurrentPlayingMusic == next && reportedPosition >= 0);
        Check(reportedPosition == initialMs && test.Player.Played.SequenceEqual(new[] { from })
            && !test.Coordinator.TryAcceptLocalEnd(new(fromId, 81)),
            $"CUE {label}: handoff keeps the incoming clock and ignores the old end without a second Play");
    }

    private static async Task CheckCueToWholeClockAsync(Music cue, Music whole, bool manual)
    {
        using var test = new Scenario();
        long fromId = await StartCueTransitionAsync(test, cue, whole);
        string label = $"CUE to whole ({(manual ? "Next" : "direct selection")}, {AppSettings.Dsp.TransitionStyle}, enabled={AppSettings.Dsp.GaplessPlayback})";
        test.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Unavailable);
        if (manual) await test.Coordinator.PlayNextAsync();
        else await test.Coordinator.PlayAtAsync(1);
        Check(test.Player.Played.SequenceEqual(new[] { cue, whole })
            && test.State.State.Playback.TransitionPresentation is { IsCommitted: true, PositionMs: 0 } presentation
            && presentation.Music == whole,
            $"{label}: ordinary fallback holds the new track clock");
        bool expectedManualRequest = manual && AppSettings.Dsp.GaplessPlayback && AppSettings.Dsp.TransitionStyle == TrackTransitionStyle.Smart;
        Check(test.Ipc.Manual.Count == (expectedManualRequest ? 1 : 0), $"{label}: manual mixing honors the selected style");

        test.Ipc.Progress = new(3, 82, 700, (long)cue.Duration.TotalMilliseconds, 0, true, PlaybackId: fromId);
        await Task.Delay(120);
        Check(test.Statistics.Starts == 1 && test.State.State.Playback.TransitionPresentation?.Music == whole,
            $"{label}: a late seek on the outgoing CUE cannot confirm the whole-file target");
        test.Ipc.Progress = new(4, 83, 5, (long)whole.Duration.TotalMilliseconds, 0, true);
        await WaitAsync(() => test.Statistics.Starts == 2 && test.State.State.Playback.TransitionPresentation is null);
        Check(test.State.CurrentPlayingMusic == whole, $"{label}: the whole-file start confirms its own clock");
    }

    private static async Task<long> StartCueTransitionAsync(Scenario test, Music from, Music next)
    {
        test.State.CurrentPlayingList = [from, next];
        test.State.CurrentPlayMode = ToolUtils.PlayMode.ListLoop;
        test.Ipc.Progress = new(1, 80, 0, 0, 0, false);
        await test.Coordinator.PlayAtAsync(0);
        long playbackId = test.Player.LastPlaybackId;
        test.Ipc.Progress = new(2, 81, 100, (long)from.Duration.TotalMilliseconds, 0, true,
            PlaybackId: from.IsCueTrack ? playbackId : 0);
        test.State.IsPlaying = true;
        await WaitAsync(() => test.Statistics.Starts == 1 && test.State.State.Playback.TransitionPresentation is null);
        return playbackId;
    }
}
