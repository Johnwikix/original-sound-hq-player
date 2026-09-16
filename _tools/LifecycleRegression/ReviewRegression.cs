using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

internal static class ReviewRegression
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    public static async Task RunAsync()
    {
        CheckNotificationThread();
        await CheckPlaybackIntentAsync();
        await CheckWatcherSettingsAsync();
    }

    private static void CheckNotificationThread()
    {
        var queue = WinUIMusicPlayer.App.MainWindow.DispatcherQueue;
        queue.OwnerThread = Environment.CurrentManagedThreadId;
        try
        {
            var state = new AppViewModel();
            using var services = new ServiceCollection().BuildServiceProvider();
            using var commands = new PlaybackCommands(new AppLifecycle(), state, services);
            int notifications = 0;
            bool wrongThread = false;
            commands.NextCommand.CanExecuteChanged += (_, _) =>
            {
                notifications++;
                wrongThread |= !queue.HasThreadAccess;
            };
            var oldList = state.CurrentPlayingList;
            var worker = new Thread(() => state.CurrentPlayingList = [new(1)]);
            worker.Start();
            worker.Join();
            Check(!wrongThread && notifications == 0, "Background list replacement published UI command notifications directly");
            queue.Drain();
            Check(notifications > 0 && !wrongThread, "UI queue did not publish availability");
            int before = notifications;
            oldList.Add(new(2));
            Check(notifications == before, "Old playlist remains subscribed");
            state.CurrentPlayingList.Clear();
            Check(notifications > before, "Replacement playlist is not observed");
            worker = new Thread(() => state.CurrentPlayingList = [new(3)]);
            worker.Start();
            worker.Join();
            commands.Dispose();
            before = notifications;
            queue.Drain();
            Check(notifications == before, "Queued notification ran after disposal");
        }
        finally { queue.OwnerThread = null; }
        Console.WriteLine("PASS: UI command notifications, playlist re-subscription and disposal of queued callbacks.");
    }

    private static async Task CheckPlaybackIntentAsync()
    {
        var lifecycle = new AppLifecycle();
        lifecycle.TransitionTo(AppPhase.Initializing);
        lifecycle.TransitionTo(AppPhase.Ready);
        var state = new AppViewModel { CurrentPlayingMusic = new(1) };
        using var services = new ServiceCollection().AddSingleton(state).AddSingleton<BassPlayerCommandService>().BuildServiceProvider();
        var player = services.GetRequiredService<BassPlayerCommandService>();
        using var commands = new PlaybackCommands(lifecycle, state, services);
        player.Pending = new TaskCompletionSource();
        var play = commands.PlayCommand.ExecuteAsync(null);
        await commands.PauseCommand.ExecuteAsync(null);
        player.Pending.SetResult();
        await play;
        Check(!state.IsPlaying && player.Toggles == 2, "Pause was lost while Play was in flight");

        player.Pending = new TaskCompletionSource();
        play = commands.PlayCommand.ExecuteAsync(null);
        await commands.PauseCommand.ExecuteAsync(null);
        Check(commands.PlayCommand.CanExecute(null), "In-flight explicit Play cannot replace the pending Pause intent");
        await commands.PlayCommand.ExecuteAsync(null);
        player.Pending.SetResult();
        await play;
        Check(state.IsPlaying && player.Toggles == 3, "Latest explicit Play intent was not retained");

        player.Pending = new TaskCompletionSource();
        var pause = commands.PauseCommand.ExecuteAsync(null);
        await commands.PlayCommand.ExecuteAsync(null);
        lifecycle.TryBeginExit(out _);
        player.Pending.SetResult();
        await pause;
        Check(!state.IsPlaying && player.Toggles == 4, "Queued playback intent executed after shutdown");
        Console.WriteLine("PASS: pending Pause, latest explicit intent and shutdown during IPC.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(30);
        Check(condition(), "Timed out waiting for watcher transition");
    }

    private static int WatcherCount(LibraryWatcherService watcher) =>
        ((System.Collections.ICollection)typeof(LibraryWatcherService)
            .GetField("_watchers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(watcher)!).Count;

    private static async Task CheckWatcherSettingsAsync()
    {
        string directory = Path.Combine(Path.GetTempPath(), "music-watcher-switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var state = new AppViewModel { IsFolderWatchEnabled = false };
        var database = new MusicDatabaseService { Folders = [new(directory)] };
        var watcher = new LibraryWatcherService(database, state, NullLogger<LibraryWatcherService>.Instance);
        try
        {
            await watcher.StartAsync();
            Check(database.Reads == 0 && WatcherCount(watcher) == 0, "Disabled startup allocated native watchers or read folders");
            state.IsFolderWatchEnabled = true;
            await WaitUntilAsync(() => WatcherCount(watcher) == 1);
            int before = AutoRescanService.Scans;
            await File.WriteAllTextAsync(Path.Combine(directory, "enabled.txt"), "enabled");
            await WaitUntilAsync(() => AutoRescanService.Scans > before);
            state.IsFolderWatchEnabled = false;
            await WaitUntilAsync(() => WatcherCount(watcher) == 0);
            before = AutoRescanService.Scans;
            await File.WriteAllTextAsync(Path.Combine(directory, "disabled.txt"), "disabled");
            await Task.Delay(1200);
            Check(AutoRescanService.Scans == before, "Disabled watcher still scans");
            state.IsFolderWatchEnabled = true;
            await WaitUntilAsync(() => WatcherCount(watcher) == 1);
            var scanStarted = new TaskCompletionSource();
            var scanCancelled = new TaskCompletionSource();
            AutoRescanService.Scan = async token =>
            {
                scanStarted.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { scanCancelled.TrySetResult(); }
            };
            await File.WriteAllTextAsync(Path.Combine(directory, "in-flight.txt"), "scanning");
            await scanStarted.Task.WaitAsync(TimeSpan.FromSeconds(4));
            state.IsFolderWatchEnabled = false;
            state.IsFolderWatchEnabled = true;
            await scanCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
            AutoRescanService.Scan = null;
            await WaitUntilAsync(() => WatcherCount(watcher) == 1);
            await watcher.StopAsync();
            state.IsFolderWatchEnabled = false;
            state.IsFolderWatchEnabled = true;
            await watcher.StartAsync();
            Check(WatcherCount(watcher) == 0, "Settings resurrected a stopped service");
        }
        finally
        {
            AutoRescanService.Scan = null;
            await watcher.StopAsync();
            Directory.Delete(directory, true);
        }

        database.Pending = new TaskCompletionSource<List<Folder>>();
        var pendingWatcher = new LibraryWatcherService(database, state, NullLogger<LibraryWatcherService>.Instance);
        var starting = pendingWatcher.StartAsync();
        var stopping = pendingWatcher.StopAsync();
        await Task.WhenAll(starting, stopping).WaitAsync(TimeSpan.FromSeconds(3));
        database.Pending.SetResult(database.Folders);
        Check(WatcherCount(pendingWatcher) == 0, "Late folder read recreated watchers after stop");
        Console.WriteLine("PASS: disabled startup, live watcher toggles, cancellation of active scan, no restart after shutdown and stop during folder read.");
    }
}
