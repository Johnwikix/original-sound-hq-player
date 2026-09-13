using System.Diagnostics;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static long ProgressTick(long ms) => ms * Stopwatch.Frequency / 1000;

    private static int ProgressWriter(string name)
    {
        using var writer = new ProgressMailbox(true, name);
        for (int i = 1; i <= 20000; i++) writer.Publish(new(0, 7, i, i * 2, i * 3, true, i * 4));
        return 0;
    }

    private static void RunProgressTests()
    {
        Run("Progress: delayed updates cannot rewind the lyric clock; stale extrapolation is bounded", () =>
        {
            var clock = new PlaybackTimeline();
            clock.Apply(new(2, 1, 1000, 10000, ProgressTick(1000), true));
            Require(clock.ReadAt(ProgressTick(1100)).curMs == 1100, "initial extrapolation wrong");
            // Old lyrics code snaps an independently advanced clock backwards on a >250ms external delta.
            // Replay delayed source timestamps and a partial correction against the shared clock instead.
            clock.Apply(new(4, 1, 1050, 10000, ProgressTick(1200), true));
            Require(clock.ReadAt(ProgressTick(1200)).curMs == 1100, "late correction rewound progress");
            Require(!clock.Apply(new(2, 1, 1000, 10000, ProgressTick(1000), true)), "stale revision accepted");
            Require(clock.ReadAt(ProgressTick(10000)).curMs == 1150, "clock ran ahead during a stall");
            clock.Apply(new(6, 1, 1400, 10000, ProgressTick(1500), true));
            Require(clock.ReadAt(ProgressTick(1500)).curMs == 1400, "clock did not catch up");
        });
        Run("Progress: seek/track epoch permits backward jumps; rapid seeks require latest acknowledgement", () =>
        {
            var clock = new PlaybackTimeline();
            clock.Apply(new(2, 1, 5000, 10000, 0, false));
            clock.BeginSeek(2000, 1);
            clock.BeginSeek(1000, 2);
            Require(!clock.Apply(new(4, 2, 2000, 10000, 0, false, 1)), "first seek overwrote newer local seek");
            Require(clock.ReadAt(0).curMs == 1000, "optimistic seek changed");
            Require(clock.Apply(new(6, 3, 1000, 10000, 0, false, 2)), "latest seek not accepted");
            Require(clock.ReadAt(0).curMs == 1000, "backward seek clamped");
            clock.Apply(new(8, 4, 0, 30000, 0, true, 2));
            Require(clock.ReadAt(0) == (0, 30000), "new track inherited previous position or duration");
        });
        Run("Progress: pause, repeated reads and duration clamp", () =>
        {
            var clock = new PlaybackTimeline();
            clock.Apply(new(2, 1, 9950, 10000, ProgressTick(1000), true));
            Require(clock.ReadAt(ProgressTick(1200)).curMs == 10000, "clock exceeded duration");
            Require(clock.ReadAt(ProgressTick(1200)).curMs == 10000, "duplicate reads integrate elapsed twice");
            clock.Apply(new(4, 2, 500, 10000, 0, false));
            Require(clock.ReadAt(ProgressTick(10000)).curMs == 500, "paused clock advances");
        });
        Run("Progress: old and new seek command serialization", () =>
        {
            byte[] bytes = new byte[BinarySerializer.ChangePositionRequestSize];
            int size = BinarySerializer.WriteChangePositionRequest(bytes, new() { PositionMs = 123, SeekId = 456 });
            Require(size == 16 && BinarySerializer.ReadChangePositionRequest(bytes).SeekId == 456, "seek id lost");
            Require(BinarySerializer.ReadChangePositionRequest(bytes.AsSpan(0, 8)).SeekId == 0, "legacy seek rejected");
        });
        Run("Progress: failed enqueue cancels only its own optimistic seek", () =>
        {
            var clock = new PlaybackTimeline();
            clock.Apply(new(2, 1, 500, 10000, 0, false));
            clock.BeginSeek(2000, 1);
            clock.BeginSeek(3000, 2);
            clock.CancelSeek(1);
            Require(clock.ReadAt(0).curMs == 3000, "old failure cancelled latest seek");
            clock.CancelSeek(2);
            Require(clock.ReadAt(0).curMs == 500, "failed enqueue left a pending clock");
        });
        Run("Progress: engine snapshot changes epoch on seek without changing output selection", () =>
        {
            var engine = Engine("WasapiShared");
            Set(engine, "_streamLock", new object());
            using var session = Source(engine, RenderKind.Pcm);
            Set(engine, "_session", session);
            Require(engine.TryCaptureProgress(out var before), "initial snapshot unavailable");
            engine.ChangeWaveChannelTime(1500, 8);
            Require(engine.TryCaptureProgress(out var after) && after.Epoch != before.Epoch
                && after.CurrentMs == 1500 && after.SeekId == 8, "seek snapshot is stale or incoherent");
        });
        Run("Progress: cross-process latest mailbox never exposes torn snapshots", () =>
        {
            string name = "ProgressTest-" + Guid.NewGuid().ToString("N");
            using var reader = new ProgressMailbox(true, name);
            Require(!reader.TryRead(out _), "unpublished mailbox is valid");
            var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--progress-writer"); info.ArgumentList.Add(name);
            using var process = Process.Start(info)!;
            try
            {
                long revision = 0, last = 0;
                var timeout = Stopwatch.StartNew();
                while (last < 20000 && timeout.ElapsedMilliseconds < 15000)
                {
                    if (!reader.TryRead(out var snapshot)) { Thread.Yield(); continue; }
                    Require(snapshot.Revision >= revision && snapshot.Epoch == 7 && snapshot.TotalMs == snapshot.CurrentMs * 2
                        && snapshot.Timestamp == snapshot.CurrentMs * 3 && snapshot.SeekId == snapshot.CurrentMs * 4, "torn or regressing snapshot");
                    revision = snapshot.Revision; last = snapshot.CurrentMs;
                }
                Require(last == 20000 && process.WaitForExit(5000) && process.ExitCode == 0, "latest snapshot lost");
            }
            finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(); } }
        });
    }
}
