using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Security.Credentials;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new TestApp();
        });
    }
}

internal sealed partial class TestApp : Application
{
    private static readonly string ResultPath = Path.Combine(AppContext.BaseDirectory, "result.txt");
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        File.WriteAllText(ResultPath, "STARTED\n");
        var window = new Window { Title = "WebDAV save responsiveness regression", Content = new TextBlock { Text = "Saving source regression" } };
        WinUIMusicPlayer.App.MainWindow = window;
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-10000, -10000));
        window.Activate();
        var source = new WebDavSource { Name = "save-regression", BaseUri = "http://127.0.0.1/dav/", UserName = "regression" };
        try
        {
            int uiThread = Environment.CurrentManagedThreadId, notifications = 0;
            var database = new MusicDatabaseService();
            var service = new WebDavLibraryService(database);
            service.SourcesChanged += () =>
            {
                Check(Environment.CurrentManagedThreadId == uiThread, "source event must run on UI thread");
                notifications++;
            };
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(15);
            long previous = Stopwatch.GetTimestamp();
            double maxGap = 0;
            timer.Tick += (_, _) =>
            {
                long now = Stopwatch.GetTimestamp();
                maxGap = Math.Max(maxGap, Stopwatch.GetElapsedTime(previous, now).TotalMilliseconds);
                previous = now;
            };
            timer.Start();
            var clock = Stopwatch.StartNew();
            var saving = service.SaveSourceAsync(source, "isolated-test-password");
            double callMs = clock.Elapsed.TotalMilliseconds;
            await saving;
            await Task.Delay(50);
            timer.Stop();
            File.WriteAllText(ResultPath, $"METRICS: saveCallMs={callMs:F1}; maxUiTickGapMs={maxGap:F1}; persistenceOnUi={database.ReadThread == uiThread}; refreshes={service.RefreshCount}\n");
            Check(database.ReadThread != uiThread, "save persistence synchronously entered UI thread");
            Check(callMs < 100 && maxGap < 250, "save blocked UI heartbeat");
            Check(service.RefreshCount == 0, "new source unnecessarily reloaded entire library");
            Check(notifications == 1 && database.WriteCount == 1, "save publishes exactly one committed source");
            source.Roots = "/dav/sub/";
            await service.SaveSourceAsync(source, "isolated-test-password");
            Check(service.RefreshCount == 1 && notifications == 2, "edited source refreshes visibility and publishes on UI");
            var stoppingDatabase = new MusicDatabaseService { CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var stoppingService = new WebDavLibraryService(stoppingDatabase);
            int lateEvents = 0;
            stoppingService.SourcesChanged += () => lateEvents++;
            var pending = stoppingService.SaveSourceAsync(source, "isolated-test-password");
            await stoppingDatabase.CommitStarted.Task;
            var stop = stoppingService.StopSavesForTestAsync();
            Check(!stop.IsCompleted, "shutdown must await in-flight persistence");
            stoppingDatabase.CommitGate.TrySetResult();
            await stop;
            await pending;
            Check(stoppingDatabase.WriteCount == 1 && lateEvents == 0, "shutdown completes persistence without late UI events");
            try { await stoppingService.SaveSourceAsync(source, "unused"); throw new Exception("save accepted after stop"); }
            catch (OperationCanceledException) { }
            var failingService = new WebDavLibraryService(new MusicDatabaseService { FailSave = true });
            int failureEvents = 0;
            failingService.SourcesChanged += () => failureEvents++;
            try { await failingService.SaveSourceAsync(source, "isolated-test-password"); throw new Exception("save failure swallowed"); }
            catch (IOException) { }
            await failingService.StopSavesForTestAsync();
            Check(failureEvents == 0, "failed save must not publish a successful source change");
            File.AppendAllText(ResultPath, "PASS: responsive save, real isolated PasswordVault write, UI notification, no new-source library reload, edited-source visibility refresh, shutdown drains persistence and suppresses late events, save failure reaches caller and does not prevent draining.\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(ResultPath, "FAIL: " + ex + "\n");
            Environment.ExitCode = 1;
        }
        finally
        {
            // Only this run's random credential key is removed; no user source or password is inspected.
            try { var vault = new PasswordVault(); vault.Remove(vault.Retrieve("OriginalSoundPlayer.WebDav", source.CredentialKey)); }
            catch { }
            window.Close();
            Exit();
        }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
