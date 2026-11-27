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
            Check(notifications == 1 && database.WriteCount == 1 && service.ScanCount == 0,
                "the general save-only entry should publish exactly once without scheduling a scan");
            source.Roots = "/dav/sub/";
            await service.SaveSourceAsync(source, "isolated-test-password");
            Check(service.RefreshCount == 1 && notifications == 2, "edited source refreshes visibility and publishes on UI");
            var stoppingDatabase = new MusicDatabaseService
            { CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously), Stored = MusicDatabaseService.Snapshot(source) };
            var stoppingService = new WebDavLibraryService(stoppingDatabase);
            int lateEvents = 0;
            stoppingService.SourcesChanged += () => lateEvents++;
            var pending = stoppingService.SaveAndScanSourceAsync(source, "isolated-test-password");
            await stoppingDatabase.CommitStarted.Task;
            var stop = stoppingService.StopSavesForTestAsync();
            Check(!stop.IsCompleted, "shutdown must await in-flight persistence");
            stoppingDatabase.CommitGate.TrySetResult();
            await stop;
            await pending;
            Check(stoppingDatabase.WriteCount == 1 && lateEvents == 0 && stoppingService.ScanCount == 0,
                "shutdown completes persistence without late UI events or scans");
            try { await stoppingService.SaveSourceAsync(source, "unused"); throw new Exception("save accepted after stop"); }
            catch (OperationCanceledException) { }
            var failingService = new WebDavLibraryService(new MusicDatabaseService { FailSave = true, Stored = MusicDatabaseService.Snapshot(source) });
            int failureEvents = 0;
            failingService.SourcesChanged += () => failureEvents++;
            try { await failingService.SaveSourceAsync(source, "replacement-test-password"); throw new Exception("save failure swallowed"); }
            catch (IOException) { }
            await failingService.StopSavesForTestAsync();
            Check(failureEvents == 0, "failed save must not publish a successful source change");
            var savedCredential = new PasswordVault().Retrieve("OriginalSoundPlayer.WebDav", source.CredentialKey);
            savedCredential.RetrievePassword();
            Check(savedCredential.Password == "isolated-test-password", "failed source edit lost the previous credential");
            await CheckSaveAndScanAsync();
            File.AppendAllText(ResultPath, "PASS: responsive save and rollback, managed save-and-scan, post-commit failures, retry, scan single flight and shutdown.\n");
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

    private static async Task CheckSaveAndScanAsync()
    {
        var bound = TimeSpan.FromSeconds(8);
        var database = new MusicDatabaseService
        {
            FailSave = true,
            CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var service = new WebDavLibraryService(database) { ScanGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var source = new WebDavSource { Name = "save and scan test", BaseUri = "http://127.0.0.1/dav/" };
        try
        {
            var failing = service.SaveAndScanSourceAsync(source, "");
            await database.CommitStarted.Task.WaitAsync(bound);
            Check(!failing.IsCompleted && service.ScanCount == 0, "scan or success escaped the durable commit gate");
            database.CommitGate.TrySetResult();
            try { await failing.WaitAsync(bound); throw new Exception("failed transaction completed as success"); }
            catch (IOException) { }
            Check(source.Id == 0 && database.WriteCount == 0 && service.ScanCount == 0, "failed transaction kept a provisional ID or started scanning");

            database.FailSave = false;
            await service.SaveAndScanSourceAsync(source, "").WaitAsync(bound);
            await service.ScanStarted.Task.WaitAsync(bound);
            Check(database.WriteCount == 1 && service.ScanCount == 1, "same snapshot could not retry a failed commit");
            var scan = service.ScanAsync(source);
            Check(ReferenceEquals(scan, service.ScanAsync(source)) && !scan.IsCompleted, "manual scan did not join the registered scan");
            service.ScanGate.TrySetResult();
            await scan.WaitAsync(bound);
        }
        finally { await service.StopSavesForTestAsync(); }

        var edit = new WebDavSource { Id = 7, Name = "edit and scan test", BaseUri = "http://127.0.0.1/dav/" };
        var editedDb = new MusicDatabaseService { Stored = MusicDatabaseService.Snapshot(edit) };
        var editing = new WebDavLibraryService(editedDb);
        editing.Projection.FailRefresh = true;
        editing.Projection.RefreshGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var publicationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editing.SourcesChanged += () =>
        {
            publicationStarted.TrySetResult();
            throw new IOException("Injected post-commit notification failure.");
        };
        try
        {
            await editing.SaveAndScanSourceAsync(edit, "").WaitAsync(bound);
            await editing.Projection.RefreshStarted.Task.WaitAsync(bound);
            await publicationStarted.Task.WaitAsync(bound);
            Check(editedDb.WriteCount == 1 && editing.ScanCount == 0, "save waited for scope refresh or scan began before the scope projection finished");
            var scan = editing.ScanAsync(edit);
            editing.Projection.RefreshGate.TrySetResult();
            await scan.WaitAsync(bound);
            Check(editing.ScanCount == 1, "an already committed source lost its scan on publication failure");
        }
        finally { await editing.StopSavesForTestAsync(); }

        var paused = new WebDavLibraryService(editedDb);
        paused.Projection.RefreshGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await paused.SaveAndScanSourceAsync(edit, "").WaitAsync(bound);
            await paused.Projection.RefreshStarted.Task.WaitAsync(bound);
            var cancel = paused.CancelScanAsync(edit.Id);
            Check(!cancel.IsCompleted, "a scan cancellation skipped publication of the committed scope");
            paused.Projection.RefreshGate.TrySetResult();
            await cancel.WaitAsync(bound);
            Check(paused.RefreshCount == 1 && paused.ScanCount == 0, "the pause did not retain the saved scope and cancel the scan");
        }
        finally { await paused.StopSavesForTestAsync(); }

        var exitingDb = new MusicDatabaseService { CommitGate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var exiting = new WebDavLibraryService(exitingDb);
        var exitingSource = new WebDavSource { Name = "save while exiting", BaseUri = "http://127.0.0.1/dav/" };
        try
        {
            int published = 0;
            exiting.SourcesChanged += () => published++;
            var save = exiting.SaveAndScanSourceAsync(exitingSource, "");
            await exitingDb.CommitStarted.Task.WaitAsync(bound);
            exiting.Projection.CanPublishState = false;
            exitingDb.CommitGate.TrySetResult();
            await save.WaitAsync(bound);
            Check(exitingDb.WriteCount == 1 && exiting.ScanCount == 0 && published == 0,
                "an accepted save did not finish cleanly before source-service shutdown");
        }
        finally { await exiting.StopSavesForTestAsync(); }
    }
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
