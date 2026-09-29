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
    private static async Task ConfirmedStartsAsync()
    {
        using var test = new Scenario();
        var music = new Music(81);
        test.State.CurrentPlayingList = [music];
        test.Ipc.Progress = new(1, 10, 0, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), false);
        await test.Coordinator.PlayAsync(music);
        await Task.Delay(75);
        Check(test.Statistics.Starts == 0, "queued/failed playback does not increment play count");
        test.Ipc.Progress = new(2, 11, 20, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(100);
        Check(test.Statistics.Starts == 1, "confirmed local playback increments once");
        test.Ipc.Progress = new(3, 12, 1000, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(75);
        Check(test.Statistics.Starts == 1, "seek and repeated telemetry do not increment play count");
        await test.Coordinator.PlayAsync(music);
        test.Ipc.Progress = new(4, 13, 20, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(100);
        Check(test.Statistics.Starts == 2, "explicit same-track restart counts a new real playback");
    }

    public static async Task RunAsync()
    {
        await ConfirmedStartsAsync();
        await CacheSizeRegression.RunAsync();
        await SingleEntryLoopAsync();
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

        using var availability = new Scenario();
        var cachedCurrent = new Music(20, 10) { IsRemoteCached = true };
        availability.State.CurrentPlayingList = [cachedCurrent, nextLocal];
        await availability.Coordinator.PlayAtAsync(0);
        availability.State.IsPlaying = true;
        var sourcesViewModel = new WebDavSourcesViewModel(new MusicDatabaseService(), availability.Library,
            availability.Remote, new WinUIMusicPlayer.Services.WebDav.RemoteAudioCache(), availability.State,
            new LibraryQueries());
        await sourcesViewModel.LoadAsync();
        cachedCurrent.IsRemoteOffline = true;
        availability.Library.PublishAvailability(10, true);
        Check(availability.Remote.PreservedStops == 0 && availability.State.IsPlaying && cachedCurrent.IsPlayable,
            "offline source status does not stop a cached current track");
        availability.Remote.Fail(cachedCurrent);
        await WaitAsync(() => availability.State.CurrentPlayingMusic == nextLocal);
        Check(availability.Player.Played.SequenceEqual([nextLocal]),
            "source read failure still advances after its availability notification");
        await sourcesViewModel.StopAsync();

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
        bounded.State.ProgressSlider = 12;
        bounded.Remote.Fail(sameSource);
        await WaitAsync(() => bounded.Player.Ends == 1);
        Check(bounded.Remote.Played.Count == 2, "decoder failures exhaust queue once instead of looping forever");
        Check(bounded.State.ProgressSlider == 12, "exhausted recovery keeps the interrupted track position visible");

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

        using var toggledPause = new Scenario();
        toggledPause.State.CurrentPlayingList = [current, offline, nextLocal];
        toggledPause.State.CurrentPlayingMusic = current;
        toggledPause.State.IsPlaying = true;
        toggledPause.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
        toggledPause.Lifecycle.TransitionTo(AppPhase.Initializing);
        toggledPause.Lifecycle.TransitionTo(AppPhase.Ready);
        var pauseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        toggledPause.Library.Probe = (_, token) => pauseProbe.Task.WaitAsync(token);
        using var pauseServices = new ServiceCollection()
            .AddSingleton(toggledPause.Player).AddSingleton(toggledPause.Remote).AddSingleton(toggledPause.Coordinator)
            .BuildServiceProvider();
        using var pauseCommands = new PlaybackCommands(toggledPause.Lifecycle, toggledPause.State, pauseServices);
        Task pendingBeforePause = toggledPause.Coordinator.PlayAsync(offline);
        while (toggledPause.Library.Probes.Count == 0) await Task.Delay(1);
        await pauseCommands.ToggleCommand.ExecuteAsync(null);
        pauseProbe.SetResult(true);
        await pendingBeforePause;
        Check(toggledPause.State.State.Playback.PendingSelection is null &&
              toggledPause.State.CurrentPlayingMusic == current && toggledPause.Remote.Played.Count == 0,
            "playback toggle pauses current track and cancels a pending remote selection");

        using var remotePause = new Scenario();
        var remoteCurrent = new Music(30, 10);
        var remotePending = new Music(31, 10);
        remotePause.State.CurrentPlayingList = [remoteCurrent, remotePending, nextLocal];
        await remotePause.Coordinator.PlayAtAsync(0);
        remotePause.State.IsPlaying = true;
        remotePause.Remote.NeedsStart = false;
        remotePause.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
        remotePause.Lifecycle.TransitionTo(AppPhase.Initializing);
        remotePause.Lifecycle.TransitionTo(AppPhase.Ready);
        var remotePauseProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        remotePause.Library.Probe = (_, token) => remotePauseProbe.Task.WaitAsync(token);
        using var remotePauseServices = new ServiceCollection()
            .AddSingleton(remotePause.Player).AddSingleton(remotePause.Remote).AddSingleton(remotePause.Coordinator)
            .BuildServiceProvider();
        using var remotePauseCommands = new PlaybackCommands(remotePause.Lifecycle, remotePause.State, remotePauseServices);
        Task remotePendingPlay = remotePause.Coordinator.PlayAsync(remotePending);
        while (remotePause.Library.Probes.Count < 2) await Task.Delay(1);
        await remotePauseCommands.ToggleCommand.ExecuteAsync(null);
        remotePauseProbe.SetResult(true);
        await remotePendingPlay;
        Check(remotePause.State.State.Playback.PendingSelection is null &&
              remotePause.State.CurrentPlayingMusic == remoteCurrent && !remotePause.Remote.WantsPlay &&
              remotePause.Remote.Played.SequenceEqual([remoteCurrent]),
            "playback toggle pauses remote current track without starting the pending remote track");

        await GaplessAsync();

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
    private static async Task GaplessAsync()
    {
        using var test = new Scenario();
        var first = new Music(101);
        var second = new Music(102);
        var third = new Music(103);
        test.State.CurrentPlayingList = [first, second, third];
        test.Ipc.Progress = new(1, 10, 0, 3000, 0, true);
        await test.Coordinator.PlayAtAsync(0);
        test.State.IsPlaying = true;
        var queued = test.Ipc.Queued.Last();
        Check(queued.Path == second.Path && queued.Token > 0, "gapless preloads the actual next queue entry");
        await Task.Yield();
        int confirmedSends = test.Ipc.Queued.Count;
        int lookups = test.State.CurrentIndexLookups;
        await Task.Delay(1100);
        Check(test.Ipc.Queued.Count == confirmedSends && test.State.CurrentIndexLookups == lookups,
            "unchanged queue ticks neither rescan entries nor resend a confirmed plan");
        int presentations = 0;
        test.Coordinator.TrackStarted += (_, _) =>
        {
            Check(App.MainWindow.DispatcherQueue.HasThreadAccess, "gapless presentation runs on UI dispatcher");
            presentations++;
        };
        // Telemetry may reach the UI before the transition notification on the other pipe.
        test.Ipc.Progress = new(2, 11, 20, 3000, 0, true, 0, 1, queued.Token);
        await Task.Delay(600);
        Check(test.State.CurrentPlayingMusic == second && test.Player.Played.Count == 1 && presentations == 1,
            "telemetry-first transition adopts presentation without replaying the track");
        await Task.Run(() => test.Ipc.Transition(queued.Token, 11));
        await Task.Delay(20);
        Check(presentations == 1, "late duplicate transition does not reload presentation");
        var thirdPlan = test.Ipc.Queued.Last();
        Check(thirdPlan.Path == third.Path && thirdPlan.Token != queued.Token, "adoption prepares the following entry");
        test.State.CurrentPlayingList.Remove(third);
        Check(test.Ipc.Queued.Any(request => request.Path.Length == 0), "queue removal cancels old preload");
        await Task.Run(() => test.Ipc.Transition(thirdPlan.Token, 12));
        await Task.Delay(20);
        Check(test.State.CurrentPlayingMusic == second, "removed queue entry cannot steal selection");
        test.State.CurrentPlayMode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.SingleLoop;
        await WaitAsync(() => test.Ipc.Queued.Last().Path == second.Path);
        Check(test.Ipc.Queued.Last().Path == second.Path, "single loop preloads the current track");
        test.State.CurrentPlayMode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RepeatOff;
        Check(test.Ipc.Queued.Last().Path.Length == 0, "repeat off clears preload");
        test.State.CurrentPlayMode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.ListLoop;
        await WaitAsync(() => test.Ipc.Queued.Last().Path.Length > 0);
        var stale = test.Ipc.Queued.Last();
        await test.Coordinator.PlayAtAsync(0);
        await Task.Run(() => test.Ipc.Transition(stale.Token, 13));
        await Task.Delay(20);
        Check(test.State.CurrentPlayingMusic == first, "explicit selection wins over a late transition");
        test.State.IsPlaying = false;
        Check(test.Ipc.Queued.Last().Path.Length == 0, "pause cancels pending preload");
        int before = test.Ipc.Queued.Count;
        await Task.Delay(550);
        Check(test.Ipc.Queued.Count == before, "paused playback stops queue refresh work");
        using var race = new Scenario();
        race.State.CurrentPlayingList = [first, second];
        race.Ipc.Progress = new(1, 20, 0, 3000, 0, true);
        await race.Coordinator.PlayAtAsync(0);
        race.State.IsPlaying = true;
        var committed = race.Ipc.Queued.Last();
        race.Ipc.Progress = new(2, 21, 10, 3000, 0, true, 0, 1, committed.Token);
        race.State.CurrentPlayingList.Remove(second);
        await Task.Delay(20);
        Check(race.State.CurrentPlayingMusic == second && race.Player.Played.Count == 1,
            "cancellation acknowledgement reconciles a transition already committed by the audio thread");

        using var batch = new Scenario();
        batch.State.CurrentPlayingList = [first, second, third];
        batch.Ipc.Progress = new(1, 30, 0, 60000, 0, true);
        await batch.Coordinator.PlayAtAsync(0);
        batch.State.IsPlaying = true;
        await Task.Yield();
        long version = batch.State.State.Queue.Version;
        int scans = batch.State.CurrentIndexLookups;
        batch.State.State.Queue.InsertNext(first, [third, second]);
        await WaitAsync(() => batch.Ipc.Queued.Last().Path.Length > 0);
        Check(batch.State.State.Queue.Version == version + 1 && batch.State.CurrentIndexLookups == scans + 1,
            "bulk insertion commits one queue version and resolves the candidate once");
        Check(batch.Ipc.Queued.Last().Path == third.Path, "bulk mutation uses the completed queue order");
        third.Path += ".renamed";
        await WaitAsync(() => batch.Ipc.Queued.Last().Path.Length > 0);
        Check(batch.Ipc.Queued.Last().Path == third.Path, "candidate path mutation invalidates its immutable plan");
        batch.State.CurrentPlayMode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RandomLoop;
        await WaitAsync(() => batch.Ipc.Queued.Last().Path.Length > 0);
        int nextIndex = PlaybackCommands.FindCandidateIndex(batch.State.CurrentPlayingList, batch.State.GetCurrentIndex(), 1);
        Check(batch.Ipc.Queued.Last().Path == batch.State.CurrentPlayingList[nextIndex].Path,
            "random mode resolves after rebuilding the real queue order");

        using var late = new Scenario();
        late.State.CurrentPlayingList = [first, second];
        late.Ipc.Progress = new(1, 40, 0, 60000, 0, true);
        var oldReply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        late.Ipc.QueueReply = () => oldReply.Task;
        await late.Coordinator.PlayAtAsync(0);
        late.State.IsPlaying = true;
        late.Ipc.QueueReply = () => Task.FromResult(true);
        await Task.Run(() => late.Ipc.RebuildOutput(1));
        await Task.Delay(30);
        int rebuiltSends = late.Ipc.Queued.Count;
        oldReply.SetResult(false);
        await Task.Delay(1200);
        Check(late.Ipc.Queued.Count == rebuiltSends && rebuiltSends == 3,
            "output generation resubmits once and a late old failure cannot invalidate the new acknowledgement");

        using var retry = new Scenario();
        retry.State.CurrentPlayingList = [first, second];
        retry.Ipc.Progress = new(1, 50, 0, 60000, 0, true);
        int attempts = 0;
        retry.Ipc.QueueReply = async () => { await Task.Yield(); return ++attempts > 1; };
        await retry.Coordinator.PlayAtAsync(0);
        retry.State.IsPlaying = true;
        await Task.Delay(1700);
        Check(attempts == 2, "rejected queue plan retries with backoff then stops after acknowledgement");
        int retryScans = retry.State.CurrentIndexLookups;
        await Task.Delay(1100);
        Check(attempts == 2 && retry.State.CurrentIndexLookups == retryScans, "retry does not turn into permanent polling work");

        using var duplicates = new Scenario();
        duplicates.State.CurrentPlayingList = [first, first, second];
        duplicates.Ipc.Progress = new(1, 60, 0, 60000, 0, true);
        await duplicates.Coordinator.PlayAtAsync(0);
        duplicates.State.IsPlaying = true;
        var duplicatePlan = duplicates.Ipc.Queued.Last();
        duplicates.Ipc.Progress = new(2, 61, 0, 60000, 0, true, 0, 1, duplicatePlan.Token);
        await Task.Run(() => duplicates.Ipc.Transition(duplicatePlan.Token, 61));
        await Task.Delay(30);
        Check(duplicates.State.State.Queue.CurrentEntryId == duplicates.State.State.Queue.EntryIdAt(1)
            && duplicates.Ipc.Queued.Last().Path == second.Path, "duplicate music keeps entry identity across gapless adoption");

        using var cancelling = new Scenario();
        cancelling.State.CurrentPlayingList = [first, second, third];
        cancelling.Ipc.Progress = new(1, 70, 0, 60000, 0, true);
        await cancelling.Coordinator.PlayAtAsync(0);
        cancelling.State.IsPlaying = true;
        var cancellingPlan = cancelling.Ipc.Queued.Last();
        var cancelReply = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        cancelling.Ipc.CancelReply = () => cancelReply.Task;
        cancelling.State.State.Queue.InsertNext(first, [third]);
        await Task.Delay(550);
        Check(cancelling.Ipc.Queued.Count == 2 && cancelling.Ipc.Queued.Last().Path.Length == 0,
            "replacement waits for cancellation identity instead of overwriting an already audible plan");
        cancelling.Ipc.Progress = new(2, 71, 0, 60000, 0, true, 0, 1, cancellingPlan.Token);
        await Task.Run(() => cancelling.Ipc.Transition(cancellingPlan.Token, 71));
        await Task.Delay(20);
        Check(cancelling.State.CurrentPlayingMusic == first && cancelling.Ipc.Queued.Count == 2,
            "transition while cancelling waits for the authoritative ordered reply");
        cancelReply.SetResult(cancellingPlan.Token);
        await WaitAsync(() => cancelling.Ipc.Queued.Count == 3);
        Check(cancelling.State.CurrentPlayingMusic == second && cancelling.Ipc.Queued.Last().Path == third.Path && cancelling.Player.Played.Count == 1,
            "late cancellation acknowledgement prepares from the adopted track without replaying it");

        using var cancelRetry = new Scenario();
        cancelRetry.State.CurrentPlayingList = [first, second, third];
        cancelRetry.Ipc.Progress = new(1, 80, 0, 60000, 0, true);
        await cancelRetry.Coordinator.PlayAtAsync(0);
        cancelRetry.State.IsPlaying = true;
        long retryToken = cancelRetry.Ipc.Queued.Last().Token;
        int cancellations = 0;
        cancelRetry.Ipc.CancelReply = async () =>
        {
            await Task.Yield();
            if (++cancellations == 1) throw new TimeoutException("lost cancellation acknowledgement");
            cancelRetry.Ipc.Progress = new(2, 81, 0, 60000, 0, true, 0, 1, retryToken);
            return retryToken;
        };
        cancelRetry.State.State.Queue.InsertNext(first, [third]);
        await WaitAsync(() => cancelRetry.State.CurrentPlayingMusic == second);
        Check(cancellations == 2 && cancelRetry.Ipc.Queued.Count == 4 && cancelRetry.Ipc.Queued.Last().Path == third.Path,
            "failed cancellation retains its identity and retries with backoff before submitting the replacement");

        using var immediate = new Scenario();
        var remote = new Music(104, 10);
        immediate.State.CurrentPlayingList = [first, second, remote];
        immediate.Ipc.Progress = new(1, 90, 0, 60000, 0, true);
        await immediate.Coordinator.PlayAtAsync(0);
        immediate.State.IsPlaying = true;
        immediate.Ipc.CancelReply = () => Task.FromResult(0L);
        var finishProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        immediate.Library.Probe = (_, token) => finishProbe.Task.WaitAsync(token);
        Task explicitSelection = immediate.Coordinator.PlayAsync(remote);
        await WaitAsync(() => immediate.Library.Probes.Count > 0);
        Check(immediate.Ipc.Queued.Count == 2 && immediate.State.State.Playback.PendingSelection?.Music == remote,
            "synchronous cancellation completion cannot preload over a new explicit selection");
        finishProbe.SetResult(true);
        await explicitSelection;
    }

    private static async Task SingleEntryLoopAsync()
    {
        foreach (var mode in new[] { WinUIMusicPlayer.Utils.ToolUtils.PlayMode.ListLoop,
                     WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RandomLoop })
        {
            using var test = new Scenario();
            var only = new Music(105);
            test.State.CurrentPlayingList = [only];
            test.State.CurrentPlayMode = mode;
            test.Ipc.Progress = new(1, 10, 0, 3000, 0, true);
            await test.Coordinator.PlayAtAsync(0);
            test.State.IsPlaying = true;
            Check(test.Ipc.Queued.Last().Path == only.Path,
                $"{mode}: gapless preloads the only song");
            Check(PlaybackCommands.FindCandidateIndex(test.State.CurrentPlayingList,
                test.State.GetCurrentIndex(), 1) == -1,
                $"{mode}: manual navigation still excludes the current entry");
            int next = PlaybackCommands.FindCandidateIndex(test.State.CurrentPlayingList,
                test.State.GetCurrentIndex(), 1, allowSingleEntryReplay: true);
            Check(next == 0, $"{mode}: natural end selects the only queue entry");
            await test.Coordinator.PlayAtAsync(next, stopWhenUnavailable: true);
            Check(test.Player.Played.SequenceEqual([only, only]),
                $"{mode}: natural end restarts the only song");

            using var gapless = new Scenario();
            gapless.State.CurrentPlayingList = [only];
            gapless.State.CurrentPlayMode = mode;
            gapless.Ipc.Progress = new(1, 10, 0, 3000, 0, true);
            await gapless.Coordinator.PlayAtAsync(0);
            int presentations = 0;
            gapless.Coordinator.TrackStarted += (_, _) => presentations++;
            gapless.State.IsPlaying = true;
            var queued = gapless.Ipc.Queued.Last();
            gapless.Ipc.Progress = new(2, 11, 0, 3000, 0, true, 0, 1, queued.Token);
            await Task.Run(() => gapless.Ipc.Transition(queued.Token, 11));
            await WaitAsync(() => presentations == 1 && gapless.Ipc.Queued.Count >= 2);
            Check(gapless.Player.Played.Count == 1 && gapless.State.CurrentPlayingMusic == only &&
                  gapless.Ipc.Queued.Last().Path == only.Path,
                $"{mode}: gapless transition presents and preloads the same song again");

            using var remote = new Scenario();
            var remoteOnly = new Music(106, 10);
            remote.State.CurrentPlayingList = [remoteOnly];
            remote.State.CurrentPlayMode = mode;
            await remote.Coordinator.PlayAtAsync(0);
            int remoteNext = PlaybackCommands.FindCandidateIndex(remote.State.CurrentPlayingList,
                remote.State.GetCurrentIndex(), 1, allowSingleEntryReplay: true);
            await remote.Coordinator.PlayAtAsync(remoteNext, stopWhenUnavailable: true);
            Check(remote.Remote.Played.SequenceEqual([remoteOnly, remoteOnly]),
                $"{mode}: natural end restarts the only remote song");
            remote.Library.Probe = (_, _) => Task.FromResult(false);
            await remote.Coordinator.PlayAtAsync(remoteNext, stopWhenUnavailable: true);
            Check(remote.Player.Ends == 1 && remote.Remote.Played.Count == 2,
                $"{mode}: unavailable remote song ends after one bounded probe");
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
    public IpcService Ipc { get; } = new();
    public PlaybackCoordinator Coordinator { get; }
    public PlaybackStatsService Statistics { get; } = new();
    public Scenario()
    {
        Player.State = State;
        Coordinator = new(State, Player, Statistics, new ApplicationTasks(Lifecycle),
            new ShutdownCoordinator(Lifecycle, NullLogger<ShutdownCoordinator>.Instance), NullLogger<PlaybackCoordinator>.Instance, Remote, Library, Ipc);
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
