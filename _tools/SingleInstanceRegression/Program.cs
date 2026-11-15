using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.ViewModel;

namespace SingleInstanceRegression;

internal static class Program
{
    internal static SingleInstanceEndpoint Endpoint = null!;
    internal static string Key = "";
    internal static string Work = "";
    internal static string Identity = "";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Key = args[0];
        Work = args[1];
        uint length = 0;
        Identity = GetCurrentPackageFullName(ref length, null) == 122 ? "packaged" : "unpackaged";
        File.WriteAllText(Path.Combine(Work, "identity-" + Environment.ProcessId + ".txt"), Identity);
        try
        {
            using var endpoint = new SingleInstanceEndpoint(Key);
            Endpoint = endpoint;
            if (args.Contains("--abandon"))
            {
                if (!endpoint.IsPrimary) return 9;
                Environment.Exit(0);
            }
            if (args.Contains("--claim")) return endpoint.IsPrimary ? 0 : 8;
            if (args.Contains("--send"))
            {
                // 错误接管不能启动另一个测试 UI，明确记录并使回归失败。
                if (endpoint.IsPrimary) return 7;
                return endpoint.SignalAsync(ActivationFileResolver.CapturePath()).GetAwaiter().GetResult() ? 0 : 5;
            }
            if (!endpoint.IsPrimary) return 6;
            endpoint.Listen();
            Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                _ = new RegressionApplication();
            });
            return Environment.ExitCode;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Work, "error-" + Environment.ProcessId + ".txt"), ex.ToString());
            return 1;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint length, StringBuilder? name);
}

public sealed class HostWindow : Window
{
    public int Activations;
    public int TaskbarRestores;
    public new void Activate() => Activations++;
    public void InitializeTaskbarHelper() => TaskbarRestores++;
}

public sealed partial class RegressionApplication : Application
{
    private readonly List<string> _results = [];
    private readonly List<string?> _received = [];
    private readonly List<Process> _children = [];
    private bool _released;
    private string ResultPath => Path.Combine(Program.Work, "result.txt");

    public RegressionApplication() => InitializeComponent();

    private void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        _results.Add("PASS: " + message);
        File.WriteAllLines(ResultPath, _results);
    }

    private Process Child(string role, string? file = null, string? key = null)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add(key ?? Program.Key);
        info.ArgumentList.Add(Program.Work);
        info.ArgumentList.Add(role);
        if (file is not null) info.ArgumentList.Add(file);
        var child = Process.Start(info)!;
        try { child.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
        _children.Add(child);
        return child;
    }

    private async Task Finished(Process child, int expected = 0)
    {
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (child.ExitCode != expected) throw new Exception($"Child {child.Id}: expected exit {expected}, actual {child.ExitCode}");
        Check(File.ReadAllText(Path.Combine(Program.Work, "identity-" + child.Id + ".txt")) == Program.Identity,
            "child retains " + Program.Identity + " process identity");
    }

    private async Task Received(int count)
    {
        for (int attempt = 0; attempt < 250; attempt++)
        {
            if (_received.Count == count) return;
            await Task.Delay(20);
        }
        throw new TimeoutException($"Expected {count} activations, actual {_received.Count}");
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        HostWindow? window = null;
        try
        {
            int uiThread = Environment.CurrentManagedThreadId;
            Check(Program.Identity == (Environment.GetCommandLineArgs().Contains("--packaged") ? "packaged" : "unpackaged"),
                "primary has requested " + Program.Identity + " identity");
            Check(ActivationFileResolver.CapturePath() == Path.Combine(Program.Work, "initial song.wav"),
                "initial file is captured before Host creation");
            window = new HostWindow();
            window.AppWindow.IsShownInSwitchers = false;
            window.AppWindow.MoveAndResize(new(-30000, -30000, 300, 200));
            window.AppWindow.Show(false);
            string song = Path.Combine(Program.Work, "歌曲 with spaces.mp3");
            File.WriteAllBytes(song, []);
            await Finished(Child("--send", song));
            Check(_received.Count == 0, "request is acknowledged while UI consumer is not attached");
            for (int index = 0; index < 17; index++) await Finished(Child("--send", song));
            var errors = new ConcurrentQueue<Exception>();
            Program.Endpoint.Attach(window.DispatcherQueue, path =>
            {
                if (Environment.CurrentManagedThreadId != uiThread) throw new Exception("Activation left UI thread");
                if (path == "fail-handler") throw new InvalidOperationException("Expected handler failure");
                _received.Add(path);
            }, errors.Enqueue);
            Check(_received.Count == 16, "buffered startup burst yields after sixteen UI deliveries");
            await Received(18);
            Check(_received.All(path => path == song), "buffered Unicode and spaced paths survive exactly once");
            await Finished(Child("--send"));
            await Received(19);
            Check(_received[18] is null, "plain launch requests SHOW without replaying the initial file");
            var burst = new[] { Child("--send", song), Child("--send", song), Child("--send", song) };
            foreach (var child in burst) await Finished(child);
            await Received(22);
            Check(Program.Endpoint.IsPrimary, "original process remains primary after concurrent second launches");

            using (var invalid = new NamedPipeClientStream(".", Program.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await invalid.ConnectAsync(1000);
                byte[] header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, int.MaxValue);
                await invalid.WriteAsync(header);
                Check(await invalid.ReadAsync(header).AsTask().WaitAsync(TimeSpan.FromSeconds(3)) == 0, "oversized request is rejected");
            }
            Check(await Program.Endpoint.SignalAsync("fail-handler"), "handler failure request is accepted by transport");
            await Finished(Child("--send", song));
            await Received(23);
            Check(errors.Count == 1 && errors.TryPeek(out var error) && error.Message == "Expected handler failure",
                "one failing handler is observed and does not strand following requests");
            using (var idle = new NamedPipeClientStream(".", Program.Endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                await idle.ConnectAsync(1000);
                await Finished(Child("--send", song));
                await Received(24);
                Check(true, "idle connection times out and next launch can still activate the owner");
            }

            var state = new AppState();
            using var shell = new ShellService(state);
            shell.Start();
            using var tray = new TrayViewModel(state.Lifecycle, new AppViewModel(),
                new WinUIMusicPlayer.DesktopLyrics.DesktopLyricsViewModel(), new PlaybackCommands(), shell);
            tray.ShowWindowCommand.Execute(null);
            Check(window.Activations == 0, "window activation is deferred until main window exists");
            WinUIMusicPlayer.App.MainWindow = window;
            window.AppWindow.Hide();
            shell.CompleteWindowCreation();
            Check(window.AppWindow.IsVisible && window.Activations == 1, "native hidden tray window is restored");
            var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
            presenter.Minimize(false);
            tray.ShowWindowCommand.Execute(null);
            Check(presenter.State == OverlappedPresenterState.Restored && window.Activations == 2, "tray command restores native minimized window");
            Check(WindowHelper.ForegroundRequests == 2 && window.TaskbarRestores == 2, "shared restore updates taskbar and requests foreground through replaced boundary");
            state.Lifecycle.TryBeginExit(out _);
            shell.ShowMainWindow();
            Check(window.Activations == 2, "stopping refuses window activation");
            await Program.Endpoint.StopAsync();
            await Finished(Child("--send", song), 5);
            Check(_received.Count == 24, "stopped listener refuses requests while mutex still prevents a replacement instance");
            Program.Endpoint.Dispose();
            _released = true;
            await Finished(Child("--claim"));
            Check(true, "new process can own mutex after actual owner disposal");
            string abandonedKey = Program.Key + ".Abandoned";
            using (var keepAlive = new Mutex(false, abandonedKey))
            {
                await Finished(Child("--abandon", key: abandonedKey));
                using var recovered = new SingleInstanceEndpoint(abandonedKey);
                Check(recovered.IsPrimary, "abandoned mutex can be recovered after a crash");
            }
            GetWindowThreadProcessId(GetForegroundWindow(), out uint foregroundProcess);
            Check(foregroundProcess != Environment.ProcessId, "fixture does not own the foreground window");
        }
        catch (Exception ex)
        {
            _results.Add("FAIL: " + ex);
            File.WriteAllLines(ResultPath, _results);
            Environment.ExitCode = 1;
        }
        finally
        {
            if (!_released) await Program.Endpoint.StopAsync();
            foreach (var child in _children)
            {
                if (!child.HasExited) child.Kill();
                child.Dispose();
            }
            window?.Close();
            File.WriteAllText(Path.Combine(Program.Work, "exit.txt"), Environment.ExitCode.ToString());
            Exit();
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
}
