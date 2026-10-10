using AudioPlayer;
using AudioPlayer.Interop;
using System.Runtime;

// Diagnostic entry point only. Production sources and NativeAOT settings are shared;
// explicit collections are requested by the probe client after stopping playback.
internal static class Program
{
    private static async Task Main()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        TaskScheduler.UnobservedTaskException += (_, e) => e.SetObserved();
        string? requestedMode = Environment.GetEnvironmentVariable("AUDIOPLAYER_PROBE_LATENCY");
        GCSettings.LatencyMode = requestedMode switch
        {
            null or "" or "interactive" => GCLatencyMode.Interactive,
            "sustained" => GCLatencyMode.SustainedLowLatency,
            _ => throw new ArgumentException("AUDIOPLAYER_PROBE_LATENCY must be interactive or sustained.")
        };
        Console.WriteLine($"[gc-mode] {GCSettings.LatencyMode},server={GCSettings.IsServerGC}");
        using var collect = new EventWaitHandle(false, EventResetMode.AutoReset,
            "Local\\AudioPlayerMemory-" + Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE"));
        using var stop = new CancellationTokenSource();
        var sampler = Task.Run(async () =>
        {
            long started = Environment.TickCount64;
            long ephemeralIndex = 0;
            long backgroundIndex = 0;
            long blockingIndex = 0;
            while (!stop.IsCancellationRequested)
            {
                if (collect.WaitOne(0))
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Console.WriteLine("[memory] diagnostic full collection completed");
                }
                long allocated = GC.GetTotalAllocatedBytes(true);
                long heap = GC.GetTotalMemory(false);
                var info = GC.GetGCMemoryInfo();
                Console.WriteLine($"[memory] {Environment.TickCount64 - started},{allocated},{heap},{info.TotalCommittedBytes},{info.FragmentedBytes},{GC.CollectionCount(0)},{GC.CollectionCount(1)},{GC.CollectionCount(2)}");
                Console.WriteLine($"[gc-pause] {Environment.TickCount64 - started},{GC.GetTotalPauseDuration().TotalMilliseconds:F4},{info.Index},{(info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0):F4},{(info.PauseDurations.Length > 1 ? info.PauseDurations[1].TotalMilliseconds : 0):F4}");
                WriteCollection(started, GCKind.Ephemeral, ref ephemeralIndex);
                WriteCollection(started, GCKind.Background, ref backgroundIndex);
                WriteCollection(started, GCKind.FullBlocking, ref blockingIndex);
                try { await Task.Delay(1000, stop.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
        Win32.timeBeginPeriod(1);
        try { await new PlayerIpcService().StartAsync(); }
        finally
        {
            stop.Cancel();
            await sampler;
            Win32.timeEndPeriod(1);
        }
    }
    // Per-kind snapshots also retain the last background collection when a later
    // ephemeral collection becomes the last collection of any kind. Sampling can
    // still miss repeated collections of the same kind between one-second ticks.
    private static void WriteCollection(long started, GCKind kind, ref long lastIndex)
    {
        GCMemoryInfo info = GC.GetGCMemoryInfo(kind);
        if (info.Index == 0 || info.Index == lastIndex) return;
        lastIndex = info.Index;
        double pause0 = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds : 0;
        double pause1 = info.PauseDurations.Length > 1 ? info.PauseDurations[1].TotalMilliseconds : 0;
        Console.WriteLine($"[gc-kind] {Environment.TickCount64 - started},{kind},{info.Index},{info.Generation},{info.Concurrent},{info.Compacted},{info.HeapSizeBytes},{info.PromotedBytes},{info.FinalizationPendingCount},{pause0:F4},{pause1:F4}");
    }
}
