using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using WinUIMusicPlayer;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.ViewModel;

#if REAL_WINUI
WinRT.ComWrappersSupport.InitializeComWrappers();
Microsoft.UI.Xaml.Application.Start(initialization =>
{
    SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
    _ = new NavigationApplication();
});
#else
using var ui = new UiContext();
SynchronizationContext.SetSynchronizationContext(ui);
App.MainWindow.DispatcherQueue.Attach();
var work = Regression.RunAsync();
ui.Run(work);
await work;

#endif

internal static class Regression
{
    public static async Task RunAsync()
    {
        using var test = new Scenario();
        var current = new Music(1);
        var offline = new Music(2, 10);
        var sameSource = new Music(3, 10);
        var nextLocal = new Music(4);
        test.State.CurrentPlayingList = [current, offline, sameSource, nextLocal];
        test.State.CurrentPlayingMusic = current;
        test.State.State.Queue.SelectEntry(1, current);
        test.Library.Probe = async (_, _) => { await Task.Delay(30); return false; };
        // Same entry point as the Next button after its cached IsPlayable check.
        await test.Coordinator.PlayAtAsync(1);
        Check(test.State.CurrentPlayingMusic == nextLocal && test.Player.Played.SequenceEqual([nextLocal]),
            "next skips a newly offline source and plays the following local track");
        Check(test.Library.Probes.Count == 1, "one failed source is probed once even before row flags update");

        using var recovered = new Scenario();
        offline.IsRemoteOffline = true;
        recovered.State.CurrentPlayingList = [current, offline, nextLocal];
        recovered.State.CurrentPlayingMusic = current;
        recovered.Library.Offline.Add(10);
        recovered.Library.Probe = async (_, token) => { await Task.Delay(10, token); return true; };
        int candidate = PlaybackCommands.FindCandidateIndex(recovered.State.CurrentPlayingList, 0, 1);
        Check(candidate == 1, "next candidate includes a WebDAV row still marked offline");
        await recovered.Coordinator.PlayAtAsync(candidate);
        Check(recovered.State.CurrentPlayingMusic == offline && recovered.Remote.Played.SequenceEqual([offline]),
            "recovered server is probed and played instead of being skipped for local audio");
        recovered.State.CurrentPlayingList = [offline, new Music(5, 10) { IsRemoteOffline = true }];
        recovered.State.CurrentPlayingMusic = offline;
        recovered.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
        recovered.Lifecycle.TransitionTo(AppPhase.Initializing);
        recovered.Lifecycle.TransitionTo(AppPhase.Ready);
        using var services = new ServiceCollection().AddSingleton(recovered.Player).AddSingleton(recovered.Remote).AddSingleton(recovered.Coordinator).BuildServiceProvider();
        using var commands = new PlaybackCommands(recovered.Lifecycle, recovered.State, services);
        Check(commands.NextCommand.CanExecute(null) && commands.PreviousCommand.CanExecute(null),
            "navigation stays enabled when all remote rows have stale offline flags");
        recovered.Library.Probe = async (_, token) => { await Task.Delay(500, token); return true; };
        Task previousCommand = commands.PreviousCommand.ExecuteAsync(null);
        await Task.Delay(20);
        Check(!previousCommand.IsCompleted && commands.PreviousCommand.CanExecute(null),
            "previous command remains clickable while its current probe is pending");
        await recovered.Coordinator.PlayAsync(nextLocal);
        await previousCommand;

        using var backwards = new Scenario();
        backwards.State.CurrentPlayingList = [nextLocal, offline, current];
        backwards.State.CurrentPlayingMusic = current;
        backwards.Library.Probe = async (_, token) => { await Task.Delay(10, token); return false; };
        await backwards.Coordinator.PlayAtAsync(1, direction: -1);
        Check(backwards.State.CurrentPlayingMusic == nextLocal, "previous continues backwards after a failed probe");

        using var exhausted = new Scenario();
        exhausted.State.CurrentPlayingList = [current, offline, sameSource, new Music(6, 20)];
        exhausted.State.CurrentPlayingMusic = current;
        exhausted.Library.Probe = async (_, token) => { await Task.Delay(10, token); return false; };
        await exhausted.Coordinator.PlayAtAsync(1, stopWhenUnavailable: true);
        Check(exhausted.Player.Ends == 1 && exhausted.Player.Played.Count == 0 && exhausted.Library.Probes.SequenceEqual([10, 20]),
            "automatic navigation ends after one bounded pass and does not restart the original track");

        using var cancelled = new Scenario();
        cancelled.State.CurrentPlayingList = [current, offline, nextLocal];
        cancelled.State.CurrentPlayingMusic = current;
        var probing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelled.Library.Probe = async (_, token) => { probing.SetResult(); await Task.Delay(Timeout.Infinite, token); return false; };
        Task oldSelection = cancelled.Coordinator.PlayAsync(offline);
        await probing.Task;
        int secondNext = PlaybackCommands.FindCandidateIndex(cancelled.State.CurrentPlayingList, cancelled.Coordinator.GetNavigationIndex(), 1);
        Check(secondNext == 2, "another Next click advances past the song being probed");
        var chosen = nextLocal;
        await cancelled.Coordinator.PlayAtAsync(secondNext);
        await oldSelection;
        Check(cancelled.State.CurrentPlayingMusic == chosen && cancelled.Player.Played.SequenceEqual([chosen]),
            "a new selection cancels the old probe without a late fallback overwriting it");

        using var rapid = new Scenario();
        rapid.State.CurrentPlayingList = [current, offline, nextLocal];
        rapid.State.CurrentPlayingMusic = current;
        Task queuedOld = rapid.Coordinator.PlayAtAsync(1);
        Task queuedNew = rapid.Coordinator.PlayAtAsync(2);
        await Task.WhenAll(queuedOld, queuedNew);
        Check(rapid.Library.Probes.Count == 0 && rapid.Player.Played.SequenceEqual([nextLocal]),
            "multiple clicks in one UI turn discard superseded work before probing starts");

        using var changedQueue = new Scenario();
        changedQueue.State.CurrentPlayingList = [current, offline, nextLocal];
        changedQueue.State.CurrentPlayingMusic = current;
        var releaseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        changedQueue.Library.Probe = (_, token) => releaseProbe.Task.WaitAsync(token);
        Task navigation = changedQueue.Coordinator.PlayAtAsync(1);
        while (changedQueue.Library.Probes.Count == 0) await Task.Delay(1);
        changedQueue.State.CurrentPlayingList = [chosen];
        releaseProbe.SetResult(false);
        await navigation;
        Check(changedQueue.Player.Played.Count == 0 && changedQueue.Remote.Played.Count == 0,
            "queue replacement during a probe invalidates the old traversal");

        using var stopping = new Scenario();
        stopping.State.CurrentPlayingList = [current, offline, nextLocal];
        stopping.State.CurrentPlayingMusic = current;
        stopping.Library.Probe = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return false; };
        Task duringShutdown = stopping.Coordinator.PlayAtAsync(1);
        while (stopping.Library.Probes.Count == 0) await Task.Delay(1);
        stopping.Lifecycle.TryBeginExit(out _);
        await duringShutdown;
        Check(stopping.Player.Played.Count == 0, "shutdown cancels pending navigation without starting another track");

        using var pending = new Scenario();
        pending.State.CurrentPlayingList = [current, offline, nextLocal];
        pending.State.CurrentPlayingMusic = current;
        var completeProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending.Library.Probe = (_, token) => completeProbe.Task.WaitAsync(token);
#if REAL_WINUI
        var listView = new Microsoft.UI.Xaml.Controls.ListView { ItemsSource = pending.State.CurrentPlayingList };
        ((Microsoft.UI.Xaml.Controls.StackPanel)App.MainWindow.Content).Children.Add(listView);
        pending.Coordinator.SelectionChanged += () => listView.SelectedIndex = pending.State.GetSelectedPlaybackIndex();
#endif
        Task pendingPlay = pending.Coordinator.PlayAsync(offline);
        Check(pending.State.SelectedPlaybackMusic == offline && pending.State.CurrentPlayingMusic == current,
            "pending selection publishes synchronously without changing actual track");
#if REAL_WINUI
        Check(ReferenceEquals(listView.SelectedItem, offline), "real ListView selects pending row before probe completes");
#endif
        completeProbe.SetResult(false);
        await pendingPlay;
        Check(pending.State.SelectedPlaybackMusic == current, "failed direct selection restores actual track highlight");
#if REAL_WINUI
        Check(ReferenceEquals(listView.SelectedItem, current), "real ListView restores current row after failed direct selection");
        ((Microsoft.UI.Xaml.Controls.StackPanel)App.MainWindow.Content).Children.Remove(listView);
#endif

        using var cached = new Scenario();
        var cachedSong = new Music(9, 10) { IsRemoteOffline = true, IsRemoteCached = true };
        cached.State.CurrentPlayingList = [current, offline, cachedSong, nextLocal];
        cached.State.CurrentPlayingMusic = current;
        cached.Library.Probe = (_, _) => Task.FromResult(false);
        await cached.Coordinator.PlayAtAsync(1);
        Check(cached.State.CurrentPlayingMusic == cachedSong && cached.Library.Probes.Count == 1,
            "failed source does not skip a complete cached song on the same source");
        await cached.Coordinator.PlayAtAsync(1);
        Check(cached.Library.Probes.Count == 1, "next request reuses source failure cooldown");
        cached.Library.Probe = (_, _) => Task.FromResult(true);
        await cached.Coordinator.PlayAsync(offline);
        Check(cached.Library.Probes.Count == 2 && cached.State.CurrentPlayingMusic == offline,
            "explicit song retry bypasses failure cooldown");

        using var failure = new Scenario();
        failure.State.CurrentPlayingList = [offline, sameSource, nextLocal];
        await failure.Coordinator.PlayAtAsync(0);
        failure.Library.Offline.Add(10);
        failure.Library.Availability.ReportFailure(10);
        failure.Remote.Fail(offline);
        await WaitAsync(() => failure.State.CurrentPlayingMusic == nextLocal);
        Check(failure.Player.Played.SequenceEqual([nextLocal]) && failure.Library.Probes.Count == 1,
            "runtime failure advances to local audio without probing failed source again");

        using var staleFailure = new Scenario();
        staleFailure.State.CurrentPlayingList = [offline, sameSource, nextLocal];
        await staleFailure.Coordinator.PlayAtAsync(0);
        var slowProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        staleFailure.Library.Probe = (_, token) => slowProbe.Task.WaitAsync(token);
        Task latest = staleFailure.Coordinator.PlayAtAsync(1);
        staleFailure.Remote.Fail(offline);
        Check(staleFailure.State.SelectedPlaybackMusic == sameSource, "old failure cannot replace a newer pending selection");
        slowProbe.SetResult(true);
        await latest;
        Check(staleFailure.State.CurrentPlayingMusic == sameSource, "new selection survives late failure callback");

        using var bounded = new Scenario();
        bounded.State.CurrentPlayingList = [offline, sameSource];
        await bounded.Coordinator.PlayAtAsync(0);
        bounded.Remote.Fail(offline);
        await WaitAsync(() => bounded.State.CurrentPlayingMusic == sameSource && bounded.State.State.Playback.PendingSelection is null);
        bounded.Remote.Fail(sameSource);
        await WaitAsync(() => bounded.Player.Ends == 1);
        Check(bounded.Remote.Played.Count == 2, "decoder failures exhaust queue once instead of looping forever");

        using var deferred = new Scenario();
        deferred.State.CurrentPlayingList = [offline, sameSource, nextLocal];
        await deferred.Coordinator.PlayAtAsync(0);
        var rejectLatest = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        deferred.Library.Probe = (_, token) => rejectLatest.Task.WaitAsync(token);
        Task rejected = deferred.Coordinator.PlayAsync(sameSource);
        deferred.Remote.Fail(offline);
        rejectLatest.SetResult(false);
        await rejected;
        await WaitAsync(() => deferred.State.CurrentPlayingMusic == nextLocal);
        Check(deferred.Player.Played.SequenceEqual([nextLocal]), "old failure resumes recovery only if newer selection also fails");

        using var stopped = new Scenario();
        stopped.State.CurrentPlayingList = [current, offline, nextLocal];
        stopped.State.CurrentPlayingMusic = current;
        var stopProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        stopped.Library.Probe = (_, token) => stopProbe.Task.WaitAsync(token);
        Task toStop = stopped.Coordinator.PlayAtAsync(1);
        while (stopped.Library.Probes.Count == 0) await Task.Delay(1);
        stopped.Coordinator.CancelPendingSelection();
        stopProbe.SetResult(true);
        await toStop;
        Check(stopped.State.SelectedPlaybackMusic == current && stopped.Remote.Played.Count == 0,
            "stop cancels pending selection so a late probe cannot restart playback");

        using var responsive = new Scenario();
        responsive.State.CurrentPlayingList = [current, offline, nextLocal];
        responsive.State.CurrentPlayingMusic = current;
        responsive.Library.Probe = async (_, token) => { await Task.Delay(350, token); return false; };
        responsive.Remote.StopDelayMs = 350; // Real cleanup can synchronously flush cached audio.
        int heartbeats = 0;
        long maxGap = 0, lastHeartbeat = Environment.TickCount64;
        using var stopHeartbeat = new CancellationTokenSource();
        async Task HeartbeatAsync()
        {
            while (!stopHeartbeat.IsCancellationRequested)
            {
                await Task.Delay(10);
                if (!App.MainWindow.DispatcherQueue.HasThreadAccess) throw new Exception("UI heartbeat escaped its dispatcher");
#if REAL_WINUI
                ((Microsoft.UI.Xaml.Controls.TextBlock)((Microsoft.UI.Xaml.Controls.StackPanel)App.MainWindow.Content).Children[1]).Text = $"UI heartbeat {heartbeats}";
#endif
                long now = Environment.TickCount64;
                maxGap = Math.Max(maxGap, now - lastHeartbeat);
                lastHeartbeat = now;
                heartbeats++;
            }
        }
        Task heartbeat = HeartbeatAsync();
        await responsive.Coordinator.PlayAtAsync(1);
        responsive.Library.Probe = (_, _) => Task.FromResult(true);
        responsive.Remote.OpenDelayMs = 350; // Real preparation can synchronously read PasswordVault.
        await responsive.Coordinator.PlayAsync(offline);
        stopHeartbeat.Cancel();
        await heartbeat;
        Check(heartbeats >= 20 && maxGap < 250, $"UI dispatcher remains responsive during probe, cleanup and preparation (ticks={heartbeats}, max gap={maxGap} ms)");
    }
    static async Task WaitAsync(Func<bool> condition)
    {
        long deadline = Environment.TickCount64 + 3000;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("regression condition");
            await Task.Delay(5);
        }
    }
    static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS: " + message); }
}
sealed class Scenario : IDisposable
{
    public AppViewModel State { get; } = new();
    public BassPlayerCommandService Player { get; } = new();
    public RemotePlaybackService Remote { get; } = new();
    public WebDavLibraryService Library { get; } = new();
    public AppLifecycle Lifecycle { get; } = new();
    public PlaybackCoordinator Coordinator { get; }
    public Scenario()
    {
        State.State.Queue.FindIndex = music => State.CurrentPlayingList.IndexOf(music);
        Coordinator = new(State, Player, new(), new ApplicationTasks(Lifecycle),
            new ShutdownCoordinator(Lifecycle, NullLogger<ShutdownCoordinator>.Instance), NullLogger<PlaybackCoordinator>.Instance, Remote, Library);
    }
    public void Dispose() => Coordinator.Dispose();
}
sealed class UiContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));
    public void Run(Task work)
    {
        long deadline = Environment.TickCount64 + 30000;
        while (!work.IsCompleted)
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException("UI regression did not settle");
            if (_queue.TryTake(out var item, 50)) item.Callback(item.State);
        }
    }
    public void Dispose() => _queue.Dispose();
}
