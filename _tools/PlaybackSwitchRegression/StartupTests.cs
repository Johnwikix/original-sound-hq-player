using System.Diagnostics;
using AudioPlayer;
using BassPlayerIpc.Shared;
using FFmpeg.AutoGen;
using FFmpeg.AutoGen.Native;

internal static unsafe partial class Program
{
    private sealed class StartupResolver(EventWaitHandle entered, EventWaitHandle release, EventWaitHandle loaded, bool fail) : WindowsFunctionResolver
    {
        private int _libraries;

        protected override nint LoadNativeLibrary(string libraryName)
        {
            if (_libraries == 0)
            {
                entered.Set();
                if (!release.WaitOne(10000)) throw new TimeoutException("Startup test did not release decoder loading");
                if (fail) throw new IOException("startup dependency failure");
            }
            nint handle = base.LoadNativeLibrary(libraryName);
            if (handle == 0) throw new DllNotFoundException(libraryName);
            if (++_libraries == 4) loaded.Set();
            return handle;
        }
    }

    private static int StartupServer(string mode)
    {
        string prefix = "AudioStartup-" + Environment.GetEnvironmentVariable("ORIGINALSOUND_IPC_SCOPE");
        using var entered = EventWaitHandle.OpenExisting(prefix + "-entered");
        using var release = EventWaitHandle.OpenExisting(prefix + "-release");
        using var loaded = EventWaitHandle.OpenExisting(prefix + "-loaded");
        using var stop = EventWaitHandle.OpenExisting(prefix + "-stop");
        using var stopping = EventWaitHandle.OpenExisting(prefix + "-stopping");
        DynamicallyLoadedBindings.FunctionResolver = new StartupResolver(entered, release, loaded, mode == "failure");
        using var service = new PlayerIpcService();
        var stopThread = new Thread(() =>
        {
            stop.WaitOne();
            service.Stop();
            stopping.Set();
        }) { IsBackground = true };
        stopThread.Start();
        try { service.StartAsync().GetAwaiter().GetResult(); }
        finally
        {
            stop.Set();
            stopThread.Join();
        }
        return 0;
    }

    private static void RunStartupTests()
    {
        Run("Startup: slow decoder finishes before either IPC handshake", () => CheckStartup("ready"));
        Run("Startup: stopping during decoder loading never publishes ready", () => CheckStartup("stop"));
        Run("Startup: decoder loading failure never publishes ready", () => CheckStartup("failure"));
    }

    private static void CheckStartup(string mode)
    {
        string scope = "startup-" + Guid.NewGuid().ToString("N");
        string prefix = "AudioStartup-" + scope;
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-entered");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-release");
        using var loaded = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-loaded");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-stop");
        using var stopping = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-stopping");
        using var alive = new Mutex(true, "WinUIMusicPlayer_SingleInstanceMutex_" + scope);
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.Environment["ORIGINALSOUND_IPC_SCOPE"] = scope;
        info.ArgumentList.Add("--startup-server");
        info.ArgumentList.Add(mode);
        using var process = Process.Start(info)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> errors = process.StandardError.ReadToEndAsync();
        using var connectTimeout = new CancellationTokenSource(10000);
        Task<PipeCommandClient>? commands = null;
        Task<PipeStateClient>? states = null;
        try
        {
            Require(entered.WaitOne(3000), "decoder was deferred until first playback instead of startup");
            commands = PipeCommandClient.ConnectAsync("OriginalSound_Audio_Control_v2_" + scope, connectTimeout.Token);
            states = PipeStateClient.ConnectAsync("OriginalSound_Audio_State_v2_" + scope, connectTimeout.Token);
            Thread.Sleep(200);
            Require(!commands.IsCompleted && !states.IsCompleted, "IPC reported ready while decoder loading was blocked");
            if (mode == "stop")
            {
                stop.Set();
                Require(stopping.WaitOne(3000), "stopping was blocked by native decoder loading");
                Require(!process.HasExited, "shutdown abandoned the in-flight native loader");
            }
            release.Set();
            if (mode == "ready")
            {
                using var client = commands.GetAwaiter().GetResult();
                using var state = states.GetAwaiter().GetResult();
                Require(loaded.WaitOne(0), "IPC connected before all four decoder libraries loaded");
                Require(client.InstanceId == state.InstanceId, "startup pipes belong to different players");
                state.Start();
                Require(SpinWait.SpinUntil(() => state.CurrentDspState is not null, 3000), "ready decoder lost initial DSP state");
                byte[] response = new byte[DspProtocol.StateSize];
                Require(client.RequestAsync(CommandId.GetDspState, [], response).GetAwaiter().GetResult().Type == MessageTypeId.DspState,
                    "commands unavailable after decoder initialization");
                stop.Set();
            }
            Require(process.WaitForExit(5000), "startup did not finish shutdown");
            Require(process.ExitCode == 0, "startup process failed: " + errors.GetAwaiter().GetResult());
            if (mode != "ready")
                Require(!commands.IsCompletedSuccessfully && !states.IsCompletedSuccessfully, "stopped or failed initialization published IPC ready");
            if (mode == "failure")
                Require(output.GetAwaiter().GetResult().Contains("startup dependency failure", StringComparison.Ordinal), "dependency failure was not observed");
        }
        finally
        {
            stop.Set();
            release.Set();
            connectTimeout.Cancel();
            if (commands is not null)
            {
                try { commands.GetAwaiter().GetResult().Dispose(); }
                catch (OperationCanceledException) { }
            }
            if (states is not null)
            {
                try { states.GetAwaiter().GetResult().Dispose(); }
                catch (OperationCanceledException) { }
            }
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                process.WaitForExit();
            }
            output.GetAwaiter().GetResult();
            errors.GetAwaiter().GetResult();
            alive.ReleaseMutex();
        }
    }
}
