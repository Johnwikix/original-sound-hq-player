using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;

internal static class PlaybackPresentationClockTests
{
    public static void Run()
    {
        var clock = new PlaybackPresentationClock();
        var music = new Music(20);
        var outgoing = new ProgressSnapshot(1, 10, 61_000, 90_000, 5000, true);
        clock.Apply(outgoing);
        var first = new PlaybackTransitionPresentation(music, 2, DurationMs: 80_000);
        clock.SetTarget(first);
        Check(0, 80_000);
        clock.SetTarget(first with { Token = 100, Epoch = 11 });
        var overlap = outgoing with { Revision = 2, TransitionToken = 100, TransitionEpoch = 11,
            TransitionCurrentMs = 700, TransitionTotalMs = 80_000 };
        clock.Apply(overlap);
        Check(700, 80_000);
        if (clock.Confirmed.ReadAt(5000).curMs != 61_000) throw new Exception("Preview changed confirmed time");
        var latest = new PlaybackTransitionPresentation(music, 4, 102, 13, DurationMs: 40_000);
        clock.SetTarget(latest);
        clock.Apply(overlap with { Revision = 3, TransitionCurrentMs = 1200 });
        Check(0, 40_000);
        clock.SetTarget(latest with { IsCommitted = true, PositionMs = 1500 });
        Check(1500, 40_000);
        clock.Apply(new ProgressSnapshot(4, 13, 1600, 40_000, 5000, true, GaplessToken: 102));
        Check(1600, 40_000);
        clock.SetTarget(first);
        Check(0, 80_000);
        clock.SetTarget(null);
        Check(1600, 40_000);
        Console.WriteLine("PASS displayed clock: immediate reset, stale overlap, confirmed timeline, late telemetry and rollback");
        void Check(long position, long duration)
        {
            var actual = clock.ReadAt(5000);
            if (actual != (position, duration)) throw new Exception($"Expected {(position, duration)}, got {actual}");
        }
    }
}
