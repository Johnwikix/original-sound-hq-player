using AudioPlayer;
using AudioPlayer.Interop;
using System.Runtime;
using System.Runtime.InteropServices;

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
        // FFmpeg DLL 单套分发（应用根目录），本进程在 Player\ 子目录。AutoGen 的
        // FunctionResolverBase 按 RootPath 全路径加载（默认=exe 目录），指向应用根即可
        string appRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
        if (File.Exists(Path.Combine(appRoot, "avcodec-63.dll")))
            FFmpeg.AutoGen.ffmpeg.RootPath = appRoot;
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
