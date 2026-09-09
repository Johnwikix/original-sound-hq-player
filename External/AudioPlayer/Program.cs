using AudioPlayer;
using AudioPlayer.Interop;
using System.Runtime;

public static class Program
{
    public static async Task Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            try { e.SetObserved(); } catch { }
        };
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        Win32.timeBeginPeriod(1);
        try
        {
            var ipcService = new PlayerIpcService();
            await ipcService.StartAsync();
        }
        catch { }
        finally
        {
            Win32.timeEndPeriod(1);
        }
    }
}
