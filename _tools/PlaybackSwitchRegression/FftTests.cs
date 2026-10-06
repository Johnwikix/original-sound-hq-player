using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static partial class Program
{
    private static void RunFftTests()
    {
        Run("FFT: stereo PCM produces independent L/R spectra", CheckFftStereo);
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

}
