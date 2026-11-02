using AudioPlayer.Playback;
using BassPlayerIpc.Shared;

internal static unsafe partial class Program
{
    private static void RunCacheLocationTests()
    {
        Run("Cache IPC: Unicode long paths, legacy payload, truncated tail", () =>
        {
            string root = "D:\\缓存\\" + new string('测', 1200);
            var settings = new IpcSetting { OutputMode = "WasapiShared", CacheRoot = root };
            var payload = new byte[BinarySerializer.IpcSettingSize + 4 + System.Text.Encoding.UTF8.GetByteCount(root)];
            int size = BinarySerializer.WriteIpcSetting(payload, settings);
            Require(BinarySerializer.ReadIpcSetting(payload.AsSpan(0, size)).CacheRoot == root, "Cache root lost in IPC");
            settings.CacheRoot = null;
            int legacy = BinarySerializer.WriteIpcSetting(payload, settings);
            Require(BinarySerializer.ReadIpcSetting(payload.AsSpan(0, legacy)).CacheRoot is null, "Legacy payload must keep fallback behavior");
            bool rejected = false;
            try { BinarySerializer.ReadIpcSetting(payload.AsSpan(0, size - 1)); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "Truncated cache path accepted");
        });
        Run("Loudness cache: unified location and live root switch", () =>
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "loudness-location", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string source = Path.Combine(fixture, "tone.wav");
            WriteLoudnessTone(source, 0.1f);
            string first = Path.Combine(fixture, "first");
            LoudnessScanner.ConfigureCacheRoot(first);
            Require(LoudnessScanner.ScanAsync(source, 48000, 2, 88200, 0, default).GetAwaiter().GetResult() is not null, "Real scan failed");
            string firstCache = Directory.GetFiles(Path.Combine(first, "Analysis", "Loudness"), "*.bin").Single();
            DateTime modified = File.GetLastWriteTimeUtc(firstCache);
            string second = Path.Combine(fixture, "second");
            LoudnessScanner.ConfigureCacheRoot(second);
            Require(LoudnessScanner.ScanAsync(source, 48000, 2, 88200, 0, default).GetAwaiter().GetResult() is not null, "Second scan failed");
            Require(Directory.GetFiles(Path.Combine(second, "Analysis", "Loudness"), "*.bin").Length == 1, "Cache did not follow root");
            Require(File.GetLastWriteTimeUtc(firstCache) == modified, "Old location was modified after root switch");
        });
        Run("Loudness cache: root switch suppresses a queued scan's late cache write", () =>
        {
            string fixture = Path.Combine(AppContext.BaseDirectory, "loudness-switch", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(fixture);
            string source = Path.Combine(fixture, "tone.wav");
            WriteLoudnessTone(source, 0.1f);
            string first = Path.Combine(fixture, "first"), second = Path.Combine(fixture, "second");
            LoudnessScanner.ConfigureCacheRoot(first);
            var gate = (SemaphoreSlim)typeof(LoudnessScanner).GetField("Gate", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
            Task<LoudnessMeasurement?> scan;
            gate.Wait();
            try
            {
                // Invoke the real async core until its first incomplete await: the old directory
                // has been captured and the scan is waiting for the decoder gate.
                scan = (Task<LoudnessMeasurement?>)typeof(LoudnessScanner).GetMethod("ScanCoreAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.Invoke(null,
                    [source, 48000, 2, 88200, 0, CancellationToken.None, null, null, (uint)0])!;
                LoudnessScanner.ConfigureCacheRoot(second);
            }
            finally { gate.Release(); }
            Require(scan.GetAwaiter().GetResult() is not null, "Queued scan lost its usable measurement");
            Require(!Directory.Exists(first) && !Directory.Exists(second), "Queued scan published a cache after the root changed");
        });
        RunDspTests();
    }
}
