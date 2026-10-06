using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static partial class Program
{
    private static void RunFftTests()
    {
        Run("FFT: stereo PCM produces independent L/R spectra", CheckFftStereo);
        Run("FFT: disabled engine releases analyzer and resumes with increasing sequence", CheckFftLifetime);
        Run("FFT: disable waits for an in-flight publisher", CheckFftStopWaitsForPublisher);
    }

    private static void CheckFftStereo()
    {
        var snapshots = new List<FftSnapshot>();
        using var available = new ManualResetEventSlim(false);
        using var analyzer = new FftAnalyzer(snapshot =>
        {
            // Analyzer-side arrays are pooled and borrowed by the publish callback.
            var owned = snapshot with
            {
                Left = snapshot.Left[..snapshot.BinCount].ToArray(),
                Right = snapshot.Right[..snapshot.BinCount].ToArray(),
            };
            lock (snapshots) snapshots.Add(owned);
            if (snapshot.IsAvailable) available.Set();
        });

        analyzer.Reset(12, 48000);
        analyzer.SetEnabled(true);
        const int block = 128;
        const int totalFrames = 4096;
        var input = new double[block * 2];
        for (int offset = 0; offset < totalFrames; offset += block)
        {
            for (int i = 0; i < block; i++)
            {
                int frame = offset + i;
                input[i * 2] = Math.Sin(2 * Math.PI * 1500 * frame / 48000);
                input[i * 2 + 1] = Math.Sin(2 * Math.PI * 3000 * frame / 48000);
            }
            analyzer.Capture(input, block, 2, 0, 48000);
        }

        Require(available.Wait(2000), "FFT worker did not publish a frame");
        FftSnapshot snapshot;
        lock (snapshots) snapshot = snapshots.Last(s => s.IsAvailable);
        int leftPeak = IndexOfPeak(snapshot.Left, snapshot.BinCount);
        int rightPeak = IndexOfPeak(snapshot.Right, snapshot.BinCount);
        Require(Math.Abs(leftPeak - 32) <= 1, $"left FFT peak was {leftPeak}, expected 32");
        Require(Math.Abs(rightPeak - 64) <= 1, $"right FFT peak was {rightPeak}, expected 64");
        Require(snapshot.Epoch == 12 && snapshot.SampleRate == 48000, "FFT metadata was not preserved");

        var payload = new byte[FftProtocol.MaxPayloadSize];
        int length = FftProtocol.Write(payload, snapshot);
        var roundTrip = FftProtocol.Read(payload.AsSpan(0, length));
        int mismatch = -1;
        for (int i = 0; i < snapshot.BinCount; i++)
            if (roundTrip.Left[i] != snapshot.Left[i]) { mismatch = i; break; }
        Require(mismatch < 0, $"left FFT IPC payload changed at {mismatch}: {roundTrip.Left[Math.Max(0, mismatch)]} vs {snapshot.Left[Math.Max(0, mismatch)]}");
        Require(roundTrip.Right.SequenceEqual(snapshot.Right[..snapshot.BinCount]), "right FFT IPC payload changed");

        analyzer.SetEnabled(false);
        lock (snapshots) Require(snapshots.Any(s => !s.IsAvailable), "disabling FFT did not publish unavailable state");
    }

    private static int IndexOfPeak(float[] values, int count)
    {
        int peak = 0;
        for (int i = 1; i < count; i++)
            if (values[i] > values[peak]) peak = i;
        return peak;
    }

    private static void CheckFftLifetime()
    {
        using var ipc = new AudioPlayer.PlayerIpcService();
        using var engine = new PlaybackEngine(ipc);
        var analyzerField = typeof(PlaybackEngine).GetField("_fftAnalyzer", Private)!;
        var analyzer = (FftAnalyzer)analyzerField.GetValue(engine)!;
        var queueField = typeof(FftAnalyzer).GetField("_leftQueue", Private)!;
        var wakeField = typeof(FftAnalyzer).GetField("_wake", Private)!;
        Require(queueField.GetValue(analyzer) is null, "disabled engine eagerly owns FFT buffers");
        Require(wakeField.GetValue(analyzer) is null, "disabled engine eagerly owns FFT wake handle");
        long sequence = 0;
        for (int i = 0; i < 5; i++)
        {
            var weak = EnableAndReleaseFft(engine, analyzer, queueField, ref sequence);
            Require(queueField.GetValue(analyzer) is null, "disabled engine retains FFT buffers");
            Require(wakeField.GetValue(analyzer) is null, "disabled engine retains FFT wake handle");
            // Test-only collection proves reachability; shipping code must not force a GC.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Require(!weak.IsAlive, "FFT worker or publisher retains disabled analyzer");
        }
        GC.KeepAlive(engine);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference EnableAndReleaseFft(PlaybackEngine engine, FftAnalyzer analyzer, System.Reflection.FieldInfo queueField, ref long sequence)
    {
        engine.SetFftEnabled(true);
        var queue = (Array)queueField.GetValue(analyzer)!;
        var weak = new WeakReference(queue);
        var sequenceField = typeof(FftAnalyzer).GetField("_sequence", Private)!;
        long current = (long)sequenceField.GetValue(analyzer)!;
        Require(current > sequence, "FFT sequence restarted after enabling again");
        engine.SetFftEnabled(false);
        sequence = (long)sequenceField.GetValue(analyzer)!;
        return weak;
    }

    private static void CheckFftStopWaitsForPublisher()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var analyzer = new FftAnalyzer(snapshot =>
        {
            if (!snapshot.IsAvailable) return;
            entered.Set();
            release.Wait();
        });
        analyzer.Reset(1, 48000);
        analyzer.SetEnabled(true);
        analyzer.Capture(new double[2048], 1024, 2, 0, 48000);
        Require(entered.Wait(2000), "publisher did not enter");
        var stopping = Task.Run(() => analyzer.SetEnabled(false));
        try
        {
            Require(!stopping.Wait(1200), "disable returned while FFT publisher still used buffers");
        }
        finally
        {
            release.Set();
            Require(stopping.Wait(2000), "FFT worker did not stop after publisher returned");
        }
    }

}
