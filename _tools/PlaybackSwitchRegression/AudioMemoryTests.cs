using System.Diagnostics;
using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunAudioMemoryTests()
    {
        foreach (int channels in new[] { 1, 2, 6 })
            Run($"Native PCM ring: {channels} channels match managed wrap, seek and drain", () => CheckNativePcmRing(channels));
        Run("Native PCM ring: concurrent copies finish before close returns", CheckNativePcmCloseRace);
        Run("HTTP loudness: cancellation interrupts open and releases the scan gate", CheckLoudnessOpenCancellation);
    }

    private static void CheckNativePcmRing(int channels)
    {
        const int capacity = 17;
        using var managed = new PcmRing(channels, capacity, 3, -1);
        using var native = new PcmRing(channels, capacity, 3, -1, nativeStorage: true);
        double[] input = new double[capacity * channels];
        double[] expected = new double[input.Length];
        double[] actual = new double[input.Length];
        var random = new Random(71);
        for (int step = 0; step < 2000; step++)
        {
            if (step % 211 == 0)
            {
                long oldEpoch = native.Epoch;
                managed.BeginSession();
                native.BeginSession();
                Require(!native.Push(input, 1, static () => false, oldEpoch), "native ring accepted a stale decoded block after seek");
            }
            int frames = Math.Min(capacity - managed.ReadyFrames, random.Next(1, capacity + 1));
            for (int sample = 0; sample < input.Length; sample++) input[sample] = (step * input.Length + sample) / 65536.0;
            Require(managed.Push(input, frames, static () => false) && native.Push(input, frames, static () => false), "live ring rejected a decoded block");
            frames = random.Next(1, capacity + 1);
            Require(managed.Render(expected, frames) == native.Render(actual, frames), "native ring consumed a different number of frames");
            Require(expected.AsSpan(0, frames * channels).SequenceEqual(actual.AsSpan(0, frames * channels)), "native copy changed PCM samples or underrun silence");
            Require(managed.ReadyFrames == native.ReadyFrames && managed.FramesPlayed == native.FramesPlayed
                && managed.UnderrunCallbacks == native.UnderrunCallbacks && managed.IsBuffering == native.IsBuffering,
                "native ring changed progress or buffering state");
        }
        managed.MarkInputEnded();
        native.MarkInputEnded();
        Require(managed.Render(expected, capacity) == native.Render(actual, capacity) && expected.AsSpan().SequenceEqual(actual),
            "native ring changed the final drain");
        Require(managed.IsDrained && native.IsDrained, "native ring failed to drain at EOF");
    }

    private static void CheckNativePcmCloseRace()
    {
        for (int cycle = 0; cycle < 100; cycle++)
        {
            using var ring = new PcmRing(2, 17, 0, -1, nativeStorage: true);
            using var entered = new CountdownEvent(2);
            double[] input = Enumerable.Repeat(0.25, 34).ToArray();
            double[] output = new double[input.Length];
            int closed = 0;
            var producer = Task.Run(() =>
            {
                entered.Signal();
                while (ring.Push(input, 17, () => Volatile.Read(ref closed) != 0)) { }
            });
            var consumer = Task.Run(() =>
            {
                entered.Signal();
                while (Volatile.Read(ref closed) == 0)
                {
                    ring.Render(output, 17);
                    Require(output.All(sample => sample is 0 or 0.25), "native ring returned corrupt PCM during close");
                }
            });
            try
            {
                Require(entered.Wait(2000), "native ring workers did not start");
                Require(SpinWait.SpinUntil(() => ring.FramesPlayed > 0, 2000), "native ring workers did not exchange samples");
                ring.Dispose();
                Volatile.Write(ref closed, 1);
                Require(Task.WaitAll([producer, consumer], 2000), "native ring retained a producer or consumer after close");
                Require(!ring.Push(input, 17, static () => false), "closed native ring accepted a late write");
                Require(ring.Render(output, 17) == 0 && output.All(sample => sample == 0), "closed native ring did not render silence");
            }
            finally
            {
                Volatile.Write(ref closed, 1);
                ring.Dispose();
                Task.WaitAll([producer, consumer], 2000);
            }
        }
    }

    private static void CheckLoudnessOpenCancellation()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "loudness-cancel-" + Guid.NewGuid().ToString("N"));
        string local = Path.Combine(directory, "reference.wav");
        Directory.CreateDirectory(directory);
        using var server = new DsfHttpFixture { Stall = true };
        using var stop = new CancellationTokenSource();
        Task<LoudnessMeasurement?>? scan = null;
        try
        {
            var source = DsfSource(server) with
            {
                Buffer = new BufferPolicy { OpenTimeoutMs = 10000, ReadTimeoutMs = 10000, RetryCount = 0 }
            };
            scan = LoudnessScanner.ScanAsync(server.Url, 48000, 2, 88200, 0, stop.Token, directory, source);
            Require(server.Blocked.Wait(3000), "loudness scan did not reach the stalled HTTP open");
            var clock = Stopwatch.StartNew();
            stop.Cancel();
            Require(SpinWait.SpinUntil(() => scan.IsCompleted, 2000), "cancelled scan is waiting for the HTTP timeout");
            try { Require(scan.GetAwaiter().GetResult() is null, "cancelled scan produced a measurement"); }
            catch (OperationCanceledException) { }
            Console.WriteLine($"HTTP loudness cancellation completed in {clock.ElapsedMilliseconds}ms before closing the socket");
            Require(!Directory.EnumerateFiles(directory, "*.bin").Any(), "cancelled scan wrote a cache entry");

            // A fresh, uncached scan exercises the shared gate after the interrupted native open.
            WriteLoudnessTone(local, 0.1f);
            using var followupStop = new CancellationTokenSource(5000);
            var followup = LoudnessScanner.ScanAsync(local, 48000, 2, 88200, 0, followupStop.Token, directory);
            Require(followup.GetAwaiter().GetResult() is not null, "cancelled HTTP scan retained the scan gate");
        }
        finally
        {
            stop.Cancel();
            // Drain a failed native open before removing the directory it could still write to.
            if (scan != null)
            {
                try { scan.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { }
            }
            Directory.Delete(directory, recursive: true);
        }
    }
}
