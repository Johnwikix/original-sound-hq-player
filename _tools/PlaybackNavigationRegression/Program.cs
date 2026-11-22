using System.Collections.Concurrent;
using BassPlayerIpc.Shared;
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
        int confirmations = 0;
        test.Coordinator.PlaybackConfirmed += (confirmed, snapshot) =>
        {
            Check(App.MainWindow.DispatcherQueue.HasThreadAccess && confirmed == music && snapshot.Playing,
                "Last.fm playback confirmation publishes the actual occurrence on UI dispatcher");
            confirmations++;
        };
        test.State.CurrentPlayingList = [music];
        test.Ipc.Progress = new(1, 10, 0, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), false);
        await test.Coordinator.PlayAsync(music);
        await Task.Delay(75);
        Check(test.Statistics.Starts == 0, "queued/failed playback does not increment play count");
        Check(confirmations == 0, "queued playback does not publish Last.fm confirmation");
        test.Ipc.Progress = new(2, 11, 20, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(100);
        Check(test.Statistics.Starts == 1, "confirmed local playback increments once");
        Check(confirmations == 1, "confirmed playback publishes exactly one Last.fm occurrence");
        test.Ipc.Progress = new(3, 12, 1000, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(75);
        Check(test.Statistics.Starts == 1, "seek and repeated telemetry do not increment play count");
        Check(confirmations == 1, "seek does not publish a new Last.fm occurrence");
        await test.Coordinator.PlayAsync(music);
        test.Ipc.Progress = new(4, 13, 20, 10000, System.Diagnostics.Stopwatch.GetTimestamp(), true);
        await Task.Delay(100);
        Check(test.Statistics.Starts == 2, "explicit same-track restart counts a new real playback");
        Check(confirmations == 2, "explicit restart publishes a fresh Last.fm occurrence");
    }

    public static async Task RunAsync()
    {
#if !REAL_REMOTE
        PlaybackPresentationClockTests.Run();
#endif
        await SongVersionsAsync();
        await ConfirmedStartsAsync();
        await CueRangesAsync();
        await CheckHandoffOrderingAsync();
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
        int candidate = PlaybackCommands.FindCandidateIndex(recovered.State, 0, 1);
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
        int secondNext = PlaybackCommands.FindCandidateIndex(cancelled.State, cancelled.Coordinator.GetNavigationIndex(), 1);
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
        await SmartTransitionsAsync();
        await ManualNextTransitionsAsync();
        await TransitionPresentationAsync();
        await WebDavTransitionFallbackAsync();

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
    private static async Task CheckHandoffOrderingAsync()
    {
        var saved = AppSettings.Dsp;
        var a = new Music(201);
        var b = new Music(202);
        var c = new Music(203);
        async Task StartAsync(Scenario test)
        {
            test.State.CurrentPlayingList = [a, b, c];
            test.Ipc.Progress = new(1, 301, 1000, 120000, 0, true);
            await test.Coordinator.PlayAtAsync(0);
            test.State.IsPlaying = true;
        }
        try
        {
            AppSettings.Dsp = saved with { GaplessPlayback = false };
            using (var test = new Scenario())
            {
                test.State.CurrentPlayingList = [a, a, c];
                test.Ipc.Progress = new(1, 301, 1000, 120000, 0, true);
                await test.Coordinator.PlayAtAsync(0);
                long outgoing = test.Player.LastPlaybackId;
                await test.Coordinator.PlayNextAsync();
                long current = test.Player.LastPlaybackId;
                Check(current != outgoing && test.State.State.Queue.CurrentEntryId == test.State.State.Queue.EntryIdAt(1)
                    && !test.Coordinator.TryAcceptLocalEnd(new(outgoing, 301)),
                    "delayed EOF from the first occurrence cannot skip a replay of the same file");
                Check(test.Coordinator.TryAcceptLocalEnd(new(current, 302)) && !test.Coordinator.TryAcceptLocalEnd(new(current, 302)),
                    "a short track may finish before its first progress sample and advances only once");
                await test.Coordinator.AdvanceAfterEndAsync();
                Check(test.State.CurrentPlayingMusic == c && test.Player.Played.SequenceEqual([a, a, c]),
                    "the matching EOF advances exactly one queue occurrence");
            }

            using (var test = new Scenario())
            {
                await StartAsync(test);
                long current = test.Player.LastPlaybackId;
                test.Player.OnPlay = _ => throw new InvalidOperationException("IPC queue full");
                await test.Coordinator.PlayNextAsync();
                Check(test.State.CurrentPlayingMusic == a && test.Coordinator.TryAcceptLocalEnd(new(current, 301)),
                    "a rejected Play command leaves EOF ownership with the playing decoder");
            }
            using (var test = new Scenario())
            {
                var remote = new Music(210, 19);
                test.State.CurrentPlayingList = [a, remote, remote];
                await test.Coordinator.PlayAtAsync(0);
                var queue = test.State.State.Queue;
                long entry = queue.EntryIdAt(1);
                var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                test.Library.Probe = (_, token) => probe.Task.WaitAsync(token);
                var click = test.Coordinator.PlayNextAsync();
                queue.InsertNext(a, [c]);
                await WaitAsync(() => test.Library.Probes.Count == 1);
                test.State.CurrentPlayingList.Move(queue.IndexOfEntry(entry), 0);
                probe.SetResult(true);
                await click;
                Check(queue.CurrentEntryId == entry && test.Remote.Played.SequenceEqual([remote]),
                    "insertion before dispatch and reordering during a probe preserve the clicked duplicate occurrence");
            }

            AppSettings.Dsp = saved with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade };
            using (var test = new Scenario())
            {
                await StartAsync(test);
                var plan = test.Ipc.Queued.Last();
                int starts = 0;
                long startPositionMs = -1;
                test.Coordinator.TrackStarted += (_, positionMs, _) => { starts++; startPositionMs = positionMs; };
                test.Ipc.Progress = new(2, 302, 1900, 120000, 0, true, GaplessToken: plan.Token);
                test.Ipc.Transition(plan.Token, 302, 1900);
                await test.Coordinator.PlayNextAsync();
                Check(test.State.CurrentPlayingMusic == b && starts == 1 && startPositionMs == 1900 && test.Player.Played.SequenceEqual([a]),
                    "handoff before deferred Next starts publishes B once without replay");
                Check(!test.Coordinator.TryAcceptLocalEnd(new(test.Player.LastPlaybackId, 301))
                    && test.Coordinator.TryAcceptLocalEnd(new(plan.Token, 302)),
                    "the adopted decoder owns the next EOF");
            }
            using (var test = new Scenario())
            {
                await StartAsync(test);
                var plan = test.Ipc.Queued.Last();
                test.Ipc.Progress = new(2, 301, 118500, 120000, 0, true, TransitionToken: plan.Token,
                    TransitionEpoch: 302, TransitionCurrentMs: 200, TransitionTotalMs: 120000);
                await WaitAsync(() => test.State.DisplayedPlaybackMusic == b);
                var cancel = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                test.Ipc.CancelReply = () => cancel.Task;
                var click = test.Coordinator.PlayNextAsync();
                await WaitAsync(() => test.Ipc.Queued.Last().Path.Length == 0);
                test.Coordinator.CancelPendingSelection();
                test.Ipc.Progress = new(3, 302, 1900, 120000, 0, true, GaplessToken: plan.Token);
                cancel.SetResult(plan.Token);
                await click;
                await WaitAsync(() => test.State.CurrentPlayingMusic == b);
                Check(test.State.State.Queue.CurrentEntryId == test.State.State.Queue.EntryIdAt(1)
                    && test.Player.Played.SequenceEqual([a]), "cancelled C restores the confirmed natural handoff to B");
            }
            using (var test = new Scenario())
            {
                await StartAsync(test);
                test.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
                var click = test.Coordinator.PlayNextAsync();
                await WaitAsync(() => test.Ipc.Manual.Count > 0);
                long entry = test.State.State.Queue.EntryIdAt(1);
                var plan = test.Ipc.Manual.Single();
                test.State.CurrentPlayingList.Add(new Music(204));
                Check(test.State.State.Playback.PendingSelection?.EntryId == entry, "append preserves the pending Next occurrence");
                test.Ipc.Transition(plan.Token, 302, 1900);
                await click;
                Check(test.State.State.Queue.CurrentEntryId == entry && test.State.CurrentPlayingMusic == b
                    && test.Player.Played.SequenceEqual([a]), "append during overlap preserves the same decoder handoff");
            }
            using (var test = new Scenario())
            {
                await StartAsync(test);
                test.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
                var click = test.Coordinator.PlayNextAsync();
                await WaitAsync(() => test.Ipc.Manual.Count > 0);
                var queue = test.State.State.Queue;
                queue.SetRequestedVersion(queue.EntryIdAt(1), c.Id);
                await click;
                Check(test.State.State.Playback.PendingSelection == null && test.State.CurrentPlayingMusic == a
                    && test.Player.Played.SequenceEqual([a]), "repinning the target cancels its stale manual handoff");
            }
        }
        finally { AppSettings.Dsp = saved; }
    }

    private static async Task GaplessAsync()
    {
        using (var notificationFirst = new Scenario())
        {
            var old = new Music(801);
            var next = new Music(802);
            notificationFirst.State.CurrentPlayingList = [old, next];
            notificationFirst.Ipc.Progress = new(1, 80, 2500, 3000, 0, true);
            await notificationFirst.Coordinator.PlayAtAsync(0);
            notificationFirst.State.IsPlaying = true;
            await Task.Yield();
            var plan = notificationFirst.Ipc.Queued.Last();
            BassPlayerIpc.Shared.ProgressSnapshot? confirmed = null;
            notificationFirst.Coordinator.PlaybackConfirmed += (_, snapshot) => confirmed = snapshot;
            await Task.Run(() => notificationFirst.Ipc.Transition(plan.Token, 81));
            await Task.Delay(50);
            Check(notificationFirst.State.CurrentPlayingMusic == next && confirmed is { } occurrence
                && occurrence.GaplessToken == plan.Token && occurrence.CurrentMs == 0,
                "notification-first gapless confirmation uses committed token rather than old state-pipe progress");
        }
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
        test.Coordinator.TrackStarted += (_, _, _) =>
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
        int nextIndex = PlaybackCommands.FindCandidateIndex(batch.State, batch.State.GetCurrentIndex(), 1);
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
            Check(PlaybackCommands.FindCandidateIndex(test.State,
                test.State.GetCurrentIndex(), 1) == -1,
                $"{mode}: manual navigation still excludes the current entry");
            int next = PlaybackCommands.FindCandidateIndex(test.State,
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
            gapless.Coordinator.TrackStarted += (_, _, _) => presentations++;
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
            int remoteNext = PlaybackCommands.FindCandidateIndex(remote.State,
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

    private static async Task SmartTransitionsAsync()
    {
        var savedSettings = AppSettings.Dsp;
        AppSettings.Dsp = savedSettings with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Smart };
        try
        {
            using var test = new Scenario();
            var first = new Music(0) { Path = Path.GetFullPath("first.wav"), Album = " Unknown Pleasures ", AlbumArtist = " Artist ", Author = "Guest 1", TrackNumber = 4 };
            var second = new Music(0) { Path = Path.GetFullPath("second.wav"), Album = "unknown pleasures", AlbumArtist = "artist", Author = "Guest 2", TrackNumber = 5, DiskNumber = 1 };
            test.State.CurrentPlayingList = [first, second];
            test.State.CurrentPlayingMusic = first;
            test.State.State.Queue.SelectEntry(test.State.State.Queue.EntryIdAt(0), first);
            test.Ipc.Progress = new(1, 91, 1500, 100000, 0, true);
            test.Coordinator.StartGapless();
            test.State.IsPlaying = true;
            var plan = test.Ipc.Queued.Last();
            Check(test.Player.Played.Count == 0 && plan.PreserveAlbumBoundary,
                "startup playback protects a credible consecutive album pair without requiring explicit reselection");

            int sent = test.Ipc.Queued.Count;
            second.Author = "New guest";
            AppSettings.Dsp = AppSettings.Dsp with { CompressorThresholdDb = -20 };
            await Task.Delay(550);
            Check(test.Ipc.Queued.Count == sent, "unrelated preferences and artist credit shadowed by album artist keep the confirmed plan");

            async Task CheckReplacementAsync(Action change, bool preserves, string message)
            {
                var previous = test.Ipc.Queued.Last();
                change();
                await WaitAsync(() => test.Ipc.Queued.Last().Path == second.Path && test.Ipc.Queued.Last().Token != previous.Token);
                Check(test.Ipc.Queued.Last().PreserveAlbumBoundary == preserves, message);
            }

            await CheckReplacementAsync(() => second.DiskNumber = 2, false, "different discs crossfade in Smart mode");
            await CheckReplacementAsync(() => second.DiskNumber = 1, true, "correcting the disc number rebuilds the same candidate");
            await CheckReplacementAsync(() => second.TrackNumber = 7, false, "nonconsecutive album tracks crossfade");
            await CheckReplacementAsync(() => second.TrackNumber = 5, true, "corrected track numbering restores Smart protection");
            await CheckReplacementAsync(() => second.AlbumArtist = "Other artist", false, "same album title across different artists is insufficient");
            await CheckReplacementAsync(() => second.AlbumArtist = "Artist", true, "explicit album artist takes precedence over feature credits");
            await CheckReplacementAsync(() => second.Album = "Unknown Album", false, "unknown album placeholders are not credible album identity");
            first.Album = "未知专辑";
            second.Album = "未知专辑";
            await Task.Yield();
            Check(!test.Ipc.Queued.Last().PreserveAlbumBoundary, "matching fallback album labels and zero transient IDs do not imply the same release or song");
            first.Author = "Same performer";
            second.Author = "Same performer";
            first.AlbumArtist = "";
            second.AlbumArtist = "";
            first.Album = "Actual album";
            await CheckReplacementAsync(() => second.Album = "Actual album", true, "missing album artist falls back to known track artist");
            await CheckReplacementAsync(() => AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Crossfade }, true,
                "crossfade preference replaces an already confirmed Smart plan for the same track");
            await CheckReplacementAsync(() => AppSettings.Dsp = AppSettings.Dsp with { CrossfadeMilliseconds = 1300 }, true,
                "crossfade duration replaces an already confirmed plan for the same track");

            var crossfade = AppSettings.Dsp;
            sent = test.Ipc.Queued.Count;
            AppSettings.Dsp = crossfade with { TransitionStyle = TrackTransitionStyle.Smart };
            AppSettings.Dsp = crossfade;
            await Task.Delay(50);
            Check(test.Ipc.Queued.Count == sent && test.Ipc.Queued.Last().PreserveAlbumBoundary,
                "rapid style round trip keeps the confirmed plan and its album hint");

            var progress = test.Ipc.Progress;
            test.Ipc.Progress = null;
            AppSettings.Dsp = AppSettings.Dsp with { CrossfadeMilliseconds = 1700 };
            await WaitAsync(() => test.Ipc.Queued.Last().Path.Length == 0);
            test.Ipc.Progress = progress;
            await WaitAsync(() => test.Ipc.Queued.Last().Path == second.Path);
            Check(test.Ipc.Queued.Last().Path == second.Path, "a confirmed plan is invalidated even before fresh progress is available");

            using var notificationFirst = new Scenario();
            var from = new Music(201);
            var next = new Music(202) { Duration = TimeSpan.FromMinutes(1) };
            notificationFirst.State.CurrentPlayingList = [from, next];
            notificationFirst.Ipc.Progress = new(1, 92, 57000, 60000, 0, true);
            await notificationFirst.Coordinator.PlayAtAsync(0);
            notificationFirst.State.IsPlaying = true;
            var nextPlan = notificationFirst.Ipc.Queued.Last();
            ProgressSnapshot? confirmation = null;
            notificationFirst.Coordinator.PlaybackConfirmed += (music, snapshot) => { if (music == next) confirmation = snapshot; };
            await Task.Run(() => notificationFirst.Ipc.Transition(nextPlan.Token, 93, 4800));
            await WaitAsync(() => confirmation.HasValue);
            Check(confirmation is { Epoch: 93, CurrentMs: 4800, TotalMs: 60000 } && notificationFirst.State.CurrentPlayingMusic == next,
                "crossfade confirmation carries the position already rendered before matching telemetry");

            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Smart };
            test.State.CurrentPlayMode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.SingleLoop;
            await WaitAsync(() => test.Ipc.Queued.Last().Path == first.Path);
            Check(test.Ipc.Queued.Last().PreserveAlbumBoundary, "Smart mode preserves the boundary when replaying the same track");

            int cancellationAttempts = 0;
            test.Ipc.CancelReply = () => ++cancellationAttempts == 1
                ? Task.FromException<long>(new TimeoutException("lost disable acknowledgement"))
                : Task.FromResult(0L);
            AppSettings.Dsp = AppSettings.Dsp with { GaplessPlayback = false };
            await WaitAsync(() => cancellationAttempts == 2);
            int afterDisable = test.Ipc.Queued.Count;
            await Task.Delay(550);
            Check(test.Ipc.Queued.Count == afterDisable && test.Ipc.Queued.Last().Path.Length == 0,
                "disabling transitions retries an unconfirmed cancellation and stops preload work when acknowledged");
        }
        finally { AppSettings.Dsp = savedSettings; }
    }

    private static async Task CueRangesAsync()
    {
        var settings = AppSettings.Dsp;
        AppSettings.Dsp = settings with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Smart };
        try
        {
            using var test = new Scenario();
            string image = Path.GetFullPath("image.flac");
            string cue = Path.ChangeExtension(image, "cue");
            Music Clip(int track, long start) => new(300 + track)
            {
                Path = image, CuePath = cue, IsCueTrack = true, CueTrackNo = track,
                SampleRate = 44100, StartSample = start, EndSample = start + 44100,
                Duration = TimeSpan.FromSeconds(1),
            };

            var first = Clip(1, 0);
            var adjacent = Clip(2, 44100);
            var later = Clip(3, 88200);
            test.State.CurrentPlayingList = [first, adjacent, later];
            test.State.CurrentPlayingMusic = first;
            test.State.State.Queue.SelectEntry(test.State.State.Queue.EntryIdAt(0), first);
            test.Ipc.Progress = new(1, 70, 150, 1000, 0, true, PlaybackId: 70);
            test.Coordinator.StartGapless();
            test.State.IsPlaying = true;
            var plan = test.Ipc.Queued.Last();
            Check(plan.SampleRange == new AudioSampleRange(44100, 88200, 44100) && plan.PreserveAlbumBoundary,
                "adjacent CUE tracks in the same image preserve their boundary and target sample range");
            test.State.CurrentPlayingList.Remove(adjacent);
            await WaitAsync(() => test.Ipc.Queued.Last().SampleRange?.StartSample == 88200);
            Check(!test.Ipc.Queued.Last().PreserveAlbumBoundary,
                "jumping across a CUE track in the same image uses the normal transition policy");

            await test.Coordinator.PlayAsync(later);
            long expectedPlaybackId = test.Player.LastPlaybackId;
            test.Ipc.Progress = new(2, 71, 400, 1000, 0, true, PlaybackId: 70);
            await Task.Delay(100);
            Check(test.State.State.Playback.TransitionPresentation?.Music == later && test.Statistics.Starts == 0,
                "an outgoing seek to a new epoch cannot confirm a different clip from the same image");
            test.Ipc.Progress = new(3, 72, 1, 1000, 0, true, PlaybackId: expectedPlaybackId);
            await WaitAsync(() => test.State.State.Playback.TransitionPresentation == null && test.Statistics.Starts == 1);
            Check(test.State.CurrentPlayingMusic == later, "the matching occurrence confirms the selected CUE track");
        }
        finally { AppSettings.Dsp = settings; }
    }

    private static async Task ManualNextTransitionsAsync()
    {
        var savedSettings = AppSettings.Dsp;
        AppSettings.Dsp = savedSettings with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Smart };
        try
        {
            var first = new Music(211) { TrackNumber = 1 };
            var second = new Music(212) { TrackNumber = 2 };
            var third = new Music(213) { TrackNumber = 3 };
            using (var warm = await StartAsync())
            {
                var automatic = warm.Ipc.Queued.Last();
                warm.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
                using var services = new ServiceCollection().AddSingleton(warm.Player).AddSingleton(warm.Coordinator).AddSingleton(warm.Remote).BuildServiceProvider();
                using var commands = new PlaybackCommands(warm.Lifecycle, warm.State, services);
                commands.NextCommand.Execute(null);
                Check(warm.State.State.Playback.PendingSelection?.Music == second, "Next publishes the pending queue row immediately");
                await WaitAsync(() => warm.Ipc.Manual.Count > 0);
                Check(warm.Ipc.Manual[0] == automatic && automatic.PreserveAlbumBoundary, "manual Smart Next reuses the existing album target's decoder identity");
                Check(warm.Ipc.Queued.All(request => request.Path.Length > 0), "reusing the natural plan does not send cancellation");
                await WaitAsync(() => warm.Ipc.ManualPolls.Count >= 2);
                Check(warm.Ipc.Manual.Count == 1 && warm.Ipc.ManualPolls.All(poll => poll == (automatic.Epoch, automatic.Token)),
                    "manual overlap sends one path and uses compact status requests for the same token");
                int wireBytes = automatic.Write().Length;
                Console.WriteLine($"Manual status sample: {warm.Ipc.Manual.Count} path request and {warm.Ipc.ManualPolls.Count} compact polls; " +
                    $"{wireBytes + warm.Ipc.ManualPolls.Count * TransitionNowQuery.Size} payload bytes vs {wireBytes * (1 + warm.Ipc.ManualPolls.Count)} with full path retries");
                int presentations = 0;
                warm.Coordinator.PlaybackConfirmed += (music, _) => { if (music == second) presentations++; };
                warm.Ipc.Progress = new(2, 201, 400, 100000, 0, true, 0, 1, automatic.Token);
                warm.Ipc.Transition(automatic.Token, 201, 400);
                await WaitAsync(() => warm.State.CurrentPlayingMusic == second);
                warm.Ipc.Transition(automatic.Token, 201, 400);
                await Task.Delay(50);
                Check(presentations == 1 && warm.Player.Played.Count == 1 && warm.State.State.Playback.PendingSelection == null,
                    "manual overlap presents a committed track exactly once with no ordinary Play");
            }

            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Crossfade };
            using (var rapid = await StartAsync(WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RepeatOff))
            {
                rapid.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Preparing);
                Task older = rapid.Coordinator.PlayNextAsync();
                await WaitAsync(() => rapid.Ipc.Manual.Count > 0);
                var abandoned = rapid.Ipc.Manual[0];
                Task latest = rapid.Coordinator.PlayNextAsync();
                await WaitAsync(() => rapid.Ipc.Manual.Any(request => request.Path == third.Path));
                var requested = rapid.Ipc.Manual.Last();
                Check(abandoned.Token != requested.Token && requested.Path == third.Path && requested.Epoch == 200,
                    "a second click advances the pending occurrence and prepares a distinct target even with Repeat Off");
                rapid.Ipc.Transition(abandoned.Token, 201);
                await Task.Delay(35);
                Check(rapid.State.CurrentPlayingMusic == first, "a stale superseded manual notification cannot steal selection");
                rapid.Ipc.Transition(requested.Token, 202);
                await Task.WhenAll(older, latest);
                Check(rapid.State.CurrentPlayingMusic == third && rapid.Player.Played.Count == 1, "only the latest rapid Next is committed");
            }

            using (var fallback = await StartAsync())
            {
                await fallback.Coordinator.PlayNextAsync();
                Check(fallback.Player.Played.SequenceEqual([first, second]) && fallback.Ipc.Manual.Count == 1
                    && fallback.Ipc.Queued.Any(request => request.Path.Length == 0), "unsupported manual transition falls back through the ordered cancellation barrier");
            }

            using (var slowBarrier = await StartAsync())
            {
                var cancel = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                slowBarrier.Ipc.CancelReply = () => cancel.Task;
                Task selection = slowBarrier.Coordinator.PlayNextAsync();
                await WaitAsync(() => slowBarrier.Ipc.Queued.Last().Path.Length == 0);
                await Task.Delay(4200);
                Check(!selection.IsCompleted && slowBarrier.State.State.Playback.PendingSelection?.Music == second && slowBarrier.Player.Played.Count == 1,
                    "a slow ordered cancel leaves the user's Next pending past the old four-second timeout");
                cancel.SetResult(0);
                await selection;
                Check(slowBarrier.Player.Played.SequenceEqual([first, second]), "an uncommitted slow cancellation resumes the same Next exactly once");
            }

            using (var superseded = await StartAsync())
            {
                var cancel = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                superseded.Ipc.CancelReply = () => cancel.Task;
                superseded.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
                Task older = superseded.Coordinator.PlayNextAsync();
                await WaitAsync(() => superseded.Ipc.Manual.Count > 0);
                var audible = superseded.Ipc.Manual[0];
                Task latest = superseded.Coordinator.PlayNextAsync();
                await WaitAsync(() => superseded.Ipc.Queued.Last().Path.Length == 0);
                superseded.State.CurrentPlayingList.Remove(third);
                superseded.Ipc.Progress = new(2, 201, 1400, 100000, 0, true, 0, 1, audible.Token);
                cancel.SetResult(audible.Token);
                await Task.WhenAll(older, latest);
                await WaitAsync(() => superseded.State.CurrentPlayingMusic == second);
                Check(superseded.State.State.Playback.PendingSelection == null && superseded.Player.Played.Count == 1,
                    "the confirmed audible handoff survives removing the newest pending Next without replay");
            }

            using (var statusFirst = await StartAsync())
            {
                statusFirst.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Committed);
                ProgressSnapshot? confirmed = null;
                statusFirst.Coordinator.PlaybackConfirmed += (music, progress) => { if (music == second) confirmed = progress; };
                Task selection = statusFirst.Coordinator.PlayNextAsync();
                await WaitAsync(() => statusFirst.Ipc.Manual.Count > 0);
                var pending = statusFirst.Ipc.Manual[0];
                await Task.Delay(40);
                statusFirst.Ipc.Transition(pending.Token, 201, 1400);
                await selection;
                Check(confirmed is { Epoch: 201, CurrentMs: 1400 } && statusFirst.Player.Played.Count == 1,
                    "committed command reply gives the state notification time to report the actual incoming position");
            }

            using (var raced = await StartAsync())
            {
                var cancel = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                raced.Ipc.CancelReply = () => cancel.Task;
                raced.Ipc.ManualReply = _ => Task.FromException<TransitionNowStatus>(new TimeoutException());
                Task selection = raced.Coordinator.PlayNextAsync();
                await WaitAsync(() => raced.Ipc.Queued.Last().Path.Length == 0);
                var pending = raced.Ipc.Manual[0];
                raced.Ipc.Transition(pending.Token, 201, 500);
                await Task.Yield();
                Check(raced.Player.Played.Count == 1 && raced.State.CurrentPlayingMusic == first,
                    "uncertain transport result waits for the ordered acknowledgement before fallback or adoption");
                cancel.SetResult(pending.Token);
                await selection;
                Check(raced.State.CurrentPlayingMusic == second && raced.Player.Played.Count == 1,
                    "late commit acknowledgement wins over ordinary fallback playback");
            }

            using (var paused = await StartAsync())
            {
                var reply = new TaskCompletionSource<TransitionNowStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                paused.Ipc.ManualReply = _ => reply.Task;
                Task selection = paused.Coordinator.PlayNextAsync();
                await WaitAsync(() => paused.Ipc.Manual.Count > 0);
                var pending = paused.Ipc.Manual[0];
                paused.Ipc.Progress = new(2, 201, 400, 100000, 0, false, 0, 1, pending.Token);
                paused.Coordinator.CancelPendingSelection();
                paused.State.IsPlaying = false;
                reply.SetResult(TransitionNowStatus.Started);
                await selection;
                await WaitAsync(() => paused.State.CurrentPlayingMusic == second);
                Check(paused.Player.Played.Count == 1 && !paused.State.IsPlaying && paused.State.State.Playback.PendingSelection == null,
                    "pause reconciles an already committed track and prevents the pending task from falling back");
            }

            using (var adjusting = await StartAsync())
            {
                adjusting.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Started);
                Task selection = adjusting.Coordinator.PlayNextAsync();
                await WaitAsync(() => adjusting.Ipc.Manual.Count > 0);
                var pending = adjusting.Ipc.Manual[0];
                AppSettings.Dsp = AppSettings.Dsp with { CrossfadeMilliseconds = 1300 };
                await Task.Yield();
                Check(adjusting.State.State.Playback.PendingSelection?.Music == second, "editing crossfade duration keeps an explicit Next pending");
                adjusting.Ipc.Transition(pending.Token, 201, 1200);
                await selection;
                Check(adjusting.State.CurrentPlayingMusic == second && adjusting.Player.Played.Count == 1,
                    "an active overlap can commit after its duration preference changes");
            }

            using (var stopping = await StartAsync())
            {
                var reply = new TaskCompletionSource<TransitionNowStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                stopping.Ipc.ManualReply = _ => reply.Task;
                Task selection = stopping.Coordinator.PlayNextAsync();
                await WaitAsync(() => stopping.Ipc.Manual.Count > 0);
                stopping.Lifecycle.TryBeginExit(out _);
                await selection.WaitAsync(TimeSpan.FromSeconds(1));
                Check(stopping.Player.Played.Count == 1 && stopping.State.State.Playback.PendingSelection == null,
                    "shutdown releases selection waiting on a blocked manual status request without starting fallback");
            }

            using (var disabledWhilePreparing = await StartAsync())
            {
                disabledWhilePreparing.Ipc.ManualReply = _ => Task.FromResult(TransitionNowStatus.Preparing);
                Task selection = disabledWhilePreparing.Coordinator.PlayNextAsync();
                await WaitAsync(() => disabledWhilePreparing.Ipc.Manual.Count > 0);
                AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Gapless };
                await selection;
                Check(disabledWhilePreparing.Player.Played.SequenceEqual([first, second]), "disabling crossfade during preparation still plays the explicit target");
            }

            AppSettings.Dsp = AppSettings.Dsp with { TransitionStyle = TrackTransitionStyle.Gapless };
            using (var ordinary = await StartAsync())
            {
                await ordinary.Coordinator.PlayNextAsync();
                Check(ordinary.Ipc.Manual.Count == 0 && ordinary.Player.Played.SequenceEqual([first, second]),
                    "Gapless mode keeps ordinary manual Next behavior");
            }

            async Task<Scenario> StartAsync(WinUIMusicPlayer.Utils.ToolUtils.PlayMode mode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.ListLoop)
            {
                var test = new Scenario();
                test.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
                test.Lifecycle.TransitionTo(AppPhase.Initializing);
                test.Lifecycle.TransitionTo(AppPhase.Ready);
                test.State.CurrentPlayingList = [first, second, third];
                test.State.CurrentPlayMode = mode;
                test.Ipc.Progress = new(1, 200, 3000, 100000, 0, true);
                await test.Coordinator.PlayAtAsync(0);
                test.State.IsPlaying = true;
                return test;
            }
        }
        finally { AppSettings.Dsp = savedSettings; }
    }

    private static async Task TransitionPresentationAsync()
    {
        var savedSettings = AppSettings.Dsp;
        AppSettings.Dsp = savedSettings with { GaplessPlayback = true, TransitionStyle = TrackTransitionStyle.Crossfade };
        try
        {
            var from = new Music(230) { Duration = TimeSpan.FromMinutes(2) };
            var second = new Music(231) { Duration = TimeSpan.FromMinutes(3) };
            var third = new Music(232) { Duration = TimeSpan.FromMinutes(4) };
            using (var rapid = new Scenario())
            {
                // Wrap at the third click. The first and final target are the same Music at distinct entries.
                rapid.State.CurrentPlayingList = [second, from, second, third];
                await BeginAsync(rapid, 1);
                var playback = rapid.State.State.Playback;
                long origin = rapid.State.State.Queue.EntryIdAt(1);
                long firstOccurrence = rapid.State.State.Queue.EntryIdAt(2);
                long wrappedOccurrence = rapid.State.State.Queue.EntryIdAt(0);
                var displays = new List<Music?>();
                playback.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName != nameof(playback.DisplayedPlaybackMusic)) return;
                    Check(App.MainWindow.DispatcherQueue.HasThreadAccess, "transition display publishes on the UI dispatcher");
                    displays.Add(playback.DisplayedPlaybackMusic);
                };
                var firstStatus = new TaskCompletionSource<TransitionNowStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                var cancel = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
                rapid.Ipc.ManualReply = _ => rapid.Ipc.Manual.Count == 1 ? firstStatus.Task : Task.FromResult(TransitionNowStatus.Started);
                rapid.Ipc.CancelReply = () => cancel.Task;

                Task firstClick = rapid.Coordinator.PlayNextAsync();
                Check(playback.TransitionPresentation is { Token: 0 } preview && preview.EntryId == firstOccurrence
                      && playback.DisplayedPlaybackMusic == second && playback.CurrentPlayingMusic == from
                      && rapid.State.State.Queue.CurrentEntryId == origin && rapid.Statistics.Starts == 0,
                    "first Next immediately previews the queue target before command submission or playback commit");
                await WaitAsync(() => rapid.Ipc.Manual.Count == 1);
                var old = rapid.Ipc.Manual[0];
                Task secondClick = rapid.Coordinator.PlayNextAsync();
                Check(playback.DisplayedPlaybackMusic == third && playback.CurrentPlayingMusic == from,
                    "second Next immediately displays the next pending entry while the first cancel is held");
                Task thirdClick = rapid.Coordinator.PlayNextAsync();
                Check(playback.TransitionPresentation?.EntryId == wrappedOccurrence && playback.DisplayedPlaybackMusic == second
                      && playback.CurrentPlayingMusic == from && rapid.State.State.Queue.CurrentEntryId == origin,
                    "third Next wraps to the first duplicate occurrence without changing committed playback");

                firstStatus.SetResult(TransitionNowStatus.Started);
                rapid.Ipc.Progress = new(2, 301, 2000, 120000, 0, true, TransitionToken: old.Token,
                    TransitionEpoch: 302, TransitionCurrentMs: 50, TransitionTotalMs: 180000);
                rapid.Ipc.Transition(old.Token, 302, 60);
                await Task.Yield();
                Check(playback.TransitionPresentation is { Token: 0 } latestPreview && latestPreview.EntryId == wrappedOccurrence
                      && playback.CurrentPlayingMusic == from,
                    "superseded start, status, and commit events cannot overwrite the third click projection");
                cancel.SetResult(0);
                await WaitAsync(() => rapid.Ipc.Manual.Count == 2);
                var latest = rapid.Ipc.Manual[1];
                rapid.Ipc.Progress = new(3, 301, 2500, 120000, 0, true, TransitionToken: latest.Token,
                    TransitionEpoch: 303, TransitionCurrentMs: 190, TransitionTotalMs: 180000);
                await WaitAsync(() => playback.TransitionPresentation?.Token == latest.Token);
                Check(playback.TransitionPresentation is { Epoch: 303, PositionMs: 190, IsCommitted: false }
                      && rapid.State.State.Queue.CurrentEntryId == origin,
                    "first audible incoming progress attaches to the shown manual entry without committing it");
                rapid.Ipc.Transition(latest.Token, 303, 1100);
                await Task.WhenAll(firstClick, secondClick, thirdClick);
                Check(playback.TransitionPresentation is { IsCommitted: true, PositionMs: 1100 } held && held.Token == latest.Token
                      && rapid.State.State.Queue.CurrentEntryId == wrappedOccurrence && playback.CurrentPlayingMusic == second
                      && rapid.State.LyricsLoads.SequenceEqual([from]) && rapid.Player.Played.SequenceEqual([from]),
                    "manual handoff commits exact occurrence once and retains its clock while base telemetry is outgoing");
                rapid.Ipc.Progress = new(4, 303, 1120, 180000, 0, true, GaplessToken: latest.Token);
                await WaitAsync(() => playback.TransitionPresentation == null && rapid.Statistics.Starts == 1);
                Check(displays.SequenceEqual([second, third, second]), "audio handoff and telemetry catch-up produce no additional track or lyrics presentation");
            }

            using (var natural = new Scenario())
            {
                natural.State.CurrentPlayingList = [from, second, third];
                await BeginAsync(natural, 0);
                var playback = natural.State.State.Playback;
                var automatic = natural.Ipc.Queued.Last();
                Check(playback.DisplayedPlaybackMusic == from, "natural preloading alone leaves the current display in place");
                int identityChanges = 0;
                playback.PropertyChanged += (_, change) => { if (change.PropertyName == nameof(playback.TransitionPresentation)) identityChanges++; };
                natural.Ipc.Progress = new(2, 301, 119000, 120000, 0, true, TransitionToken: automatic.Token,
                    TransitionEpoch: 302, TransitionCurrentMs: 120, TransitionTotalMs: 180000);
                await WaitAsync(() => playback.DisplayedPlaybackMusic == second);
                Check(playback.TransitionPresentation is { PositionMs: 120, IsCommitted: false } && playback.CurrentPlayingMusic == from
                      && natural.Coordinator.GetNavigationIndex() == 1 && natural.Statistics.Starts == 0,
                    "natural overlap shows the incoming track at first audio and navigates from its occurrence");
                natural.Ipc.Progress = natural.Ipc.Progress!.Value with { Revision = 3, TransitionCurrentMs = 420 };
                await Task.Yield();
                Check(identityChanges == 1 && playback.TransitionPresentation?.PositionMs == 120,
                    "periodic incoming positions do not republish the transition projection");
                natural.Ipc.Progress = new(4, 301, 119450, 120000, 0, true);
                await WaitAsync(() => playback.DisplayedPlaybackMusic == from);
                Check(playback.TransitionPresentation == null && playback.CurrentPlayingMusic == from && natural.Statistics.Starts == 0,
                    "aborted natural overlap rolls display back to the still-committed outgoing track");
            }

            using (var aborted = new Scenario())
            {
                aborted.State.CurrentPlayingList = [from, second];
                await BeginAsync(aborted, 0);
                var status = new TaskCompletionSource<TransitionNowStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
                aborted.Ipc.ManualReply = _ => status.Task;
                Task pending = aborted.Coordinator.PlayNextAsync();
                await WaitAsync(() => aborted.Ipc.Manual.Count > 0);
                var old = aborted.Ipc.Manual[0];
                aborted.Coordinator.CancelPendingSelection();
                status.SetResult(TransitionNowStatus.Started);
                await pending;
                aborted.Ipc.Progress = new(2, 301, 5090, 120000, 0, true, TransitionToken: old.Token,
                    TransitionEpoch: 302, TransitionCurrentMs: 60, TransitionTotalMs: 180000);
                await Task.Yield();
                Check(aborted.State.DisplayedPlaybackMusic == from && aborted.State.State.Playback.TransitionPresentation == null
                      && aborted.State.CurrentPlayingMusic == from && aborted.Player.Played.SequenceEqual([from]),
                    "cancelling a manual preview restores the confirmed track and rejects its late overlap snapshot");
            }

            using (var webDav = new Scenario())
            {
                var remote = new Music(233, 41);
                webDav.State.CurrentPlayingList = [from, remote];
                await BeginAsync(webDav, 0);
                var probe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                webDav.Library.Probe = (_, token) => probe.Task.WaitAsync(token);
                Task pending = webDav.Coordinator.PlayNextAsync();
                await WaitAsync(() => webDav.Library.Probes.Contains(remote.SourceId));
                Check(webDav.State.DisplayedPlaybackMusic == from && webDav.State.State.Playback.TransitionPresentation == null,
                    "ordinary manual WebDAV probing retains the outgoing display");
                probe.SetResult(false);
                await pending;
                Check(webDav.State.DisplayedPlaybackMusic == from && webDav.Remote.Played.Count == 0,
                    "failed WebDAV probe preserves the committed display and playback");
            }

            using (var version = new Scenario())
            {
                var wanted = new Music(234) { SongId = 91, SongPreferredMusicId = 234, Path = MissingFile() };
                var available = new Music(235) { SongId = 91, Path = Environment.ProcessPath! };
                version.State.LibrarySongs = [from, wanted, available];
                version.State.CurrentPlayingList = [from, wanted];
                await BeginAsync(version, 0);
                var playback = version.State.State.Playback;
                var shownVersions = new List<Music?>();
                playback.PropertyChanged += (_, change) =>
                {
                    if (change.PropertyName == nameof(playback.DisplayedPlaybackMusic)) shownVersions.Add(playback.DisplayedPlaybackMusic);
                };
                Task pending = version.Coordinator.PlayNextAsync();
                Check(playback.DisplayedPlaybackMusic == wanted && playback.CurrentPlayingMusic == from,
                    "local row previews immediately before checking that its file still exists");
                await pending;
                Check(shownVersions.SequenceEqual([wanted, available]) && playback.DisplayedPlaybackMusic == available
                      && playback.CurrentPlayingMusic == available && playback.TransitionPresentation is { IsCommitted: true, Token: 0 }
                      && version.Player.Played.SequenceEqual([from, available]),
                    "preview follows the playable version and ordinary fallback keeps its clock anchored until a new epoch");
                version.Ipc.Progress = new(2, 302, 85, 60000, 0, true);
                await WaitAsync(() => playback.TransitionPresentation == null);
                Check(shownVersions.SequenceEqual([wanted, available]), "ordinary fallback clock confirmation leaves the presented version intact");
            }

            using (var unavailable = new Scenario())
            {
                var missing = new Music(236) { SongId = 92, Path = MissingFile() };
                unavailable.State.CurrentPlayingList = [from, missing];
                await BeginAsync(unavailable, 0);
                Task pending = unavailable.Coordinator.PlayNextAsync();
                Check(unavailable.State.DisplayedPlaybackMusic == missing, "manual intent appears while local eligibility is pending");
                await pending;
                Check(unavailable.State.DisplayedPlaybackMusic == from && unavailable.State.State.Playback.TransitionPresentation == null
                      && unavailable.Player.Played.SequenceEqual([from]),
                    "unavailable final local candidate rolls the preview back without initiating another playback");
            }
        }
        finally { AppSettings.Dsp = savedSettings; }

        static async Task BeginAsync(Scenario test, int index)
        {
            test.Ipc.Progress = new(1, 301, 1000, 120000, 0, true);
            await test.Coordinator.PlayAtAsync(index);
            test.State.IsPlaying = true;
        }

        static string MissingFile() => Path.Combine(Path.GetTempPath(), $"navigation-absent-{Guid.NewGuid():N}.wav");
    }

    private static async Task WebDavTransitionFallbackAsync()
    {
        var original = AppSettings.Dsp;
        try
        {
            foreach (var style in new[] { TrackTransitionStyle.Smart, TrackTransitionStyle.Crossfade })
            {
                AppSettings.Dsp = original with { TransitionStyle = style, GaplessPlayback = true };
                foreach (bool manual in new[] { false, true })
                {
                    await CheckFallbackAsync(style, manual, fromSource: 0, toSource: 31, skipOffline: true);
                    await CheckFallbackAsync(style, manual, fromSource: 31, toSource: 0);
                    await CheckFallbackAsync(style, manual, fromSource: 31, toSource: 32);
                }
                await CheckPendingEndAsync(style, fromSource: 0);
                await CheckPendingEndAsync(style, fromSource: 34);
                await CheckDeferredEndRecoveryAsync(style);
                await CheckStoppedEndAsync(style);
            }
        }
        finally { AppSettings.Dsp = original; }

        static async Task CheckFallbackAsync(TrackTransitionStyle style, bool manual, int fromSource, int toSource, bool skipOffline = false)
        {
            using var test = new Scenario();
            var from = new Music(215, fromSource);
            var to = new Music(216, toSource) { IsRemoteCached = skipOffline, IsRemoteOffline = skipOffline, IsMissing = skipOffline };
            var offline = new Music(217, toSource) { IsRemoteOffline = true };
            var sentinel = new Music(218, 33);
            test.State.CurrentPlayingList = skipOffline ? [to, from, offline, to, sentinel] : [to, from, to, sentinel];
            int targetIndex = skipOffline ? 3 : 2;
            long targetEntry = test.State.State.Queue.EntryIdAt(targetIndex);
            test.Library.Probe = (source, _) => Task.FromResult(!skipOffline || source.Id != toSource);
            await BeginAsync(test, 1);
            int localBefore = test.Player.Played.Count;
            int remoteBefore = test.Remote.Played.Count;
            int presentations = 0;
            test.Coordinator.TrackStarted += (music, _, _) => { if (music == to) presentations++; };
            if (manual)
            {
                using var services = new ServiceCollection().AddSingleton(test.Player).AddSingleton(test.Coordinator).AddSingleton(test.Remote).BuildServiceProvider();
                using var commands = new PlaybackCommands(test.Lifecycle, test.State, services);
                commands.NextCommand.Execute(null);
                await WaitAsync(() => test.State.CurrentPlayingMusic == to && test.State.State.Playback.PendingSelection == null);
            }
            else await ReportEndedAsync(test);

            string context = $"{style} {(manual ? "Next" : "ended")} {(from.IsRemote ? "WebDAV" : "local")} to {(to.IsRemote ? "WebDAV" : "local")}";
            Check(test.State.CurrentPlayingMusic == to && test.State.State.Queue.CurrentEntryId == targetEntry && presentations == 1
                  && test.Player.Played.Count - localBefore == (to.IsRemote ? 0 : 1)
                  && test.Remote.Played.Count - remoteBefore == (to.IsRemote ? 1 : 0),
                $"{context}: regular start once at the correct duplicate queue occurrence");
            Check(test.Ipc.Manual.Count == 0 && test.Ipc.ManualPolls.Count == 0 && test.Ipc.Queued.All(request => request.Path.Length == 0),
                $"{context}: no local preload, crossfade or poll for the WebDAV edge");
            if (skipOffline)
                Check(test.Library.Probes.SequenceEqual([toSource]), $"{context}: failed uncached next entry once, used same-source hot cache without a probe");
        }

        static async Task CheckPendingEndAsync(TrackTransitionStyle style, int fromSource)
        {
            using var rapid = new Scenario();
            var from = new Music(219, fromSource);
            var earlier = new Music(220, 35);
            var latest = new Music(221, 36);
            rapid.State.CurrentPlayingList = [from, earlier, latest];
            await BeginAsync(rapid, 0);
            int remoteBefore = rapid.Remote.Played.Count;
            var earlierProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var latestProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            rapid.Library.Probe = (source, token) => (source.Id == earlier.SourceId ? earlierProbe.Task : latestProbe.Task).WaitAsync(token);
            Task first = rapid.Coordinator.PlayNextAsync();
            await WaitAsync(() => rapid.Library.Probes.Contains(earlier.SourceId));
            Task last = rapid.Coordinator.PlayNextAsync();
            await WaitAsync(() => rapid.Library.Probes.Contains(latest.SourceId));
            Task ended = ReportEndedAsync(rapid);
            string sourceName = from.IsRemote ? "WebDAV" : "local";
            Check(rapid.State.State.Playback.PendingSelection?.Music == latest && rapid.Remote.Played.Count == remoteBefore,
                $"{style}: outgoing {sourceName} end keeps the newest still-probing manual Next");
            await ended;
            latestProbe.SetResult(true);
            await Task.WhenAll(first, last);
            Check(rapid.Remote.Played.Count == remoteBefore + 1 && rapid.Remote.Played.Last() == latest && rapid.State.GetCurrentIndex() == 2,
                $"{style}: deferred {sourceName} end does not advance again after manual target starts");
        }

        static async Task CheckDeferredEndRecoveryAsync(TrackTransitionStyle style)
        {
            using (var exhausted = new Scenario())
            {
                var from = new Music(222, 37);
                var unavailable = new Music(223, 38);
                exhausted.State.CurrentPlayingList = [from, unavailable];
                await BeginAsync(exhausted, 0);
                var failedProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                exhausted.Library.Probe = (_, token) => failedProbe.Task.WaitAsync(token);
                Task selection = exhausted.Coordinator.PlayNextAsync();
                await WaitAsync(() => exhausted.Library.Probes.Contains(unavailable.SourceId));
                await ReportEndedAsync(exhausted);
                failedProbe.SetResult(false);
                await selection;
                await WaitAsync(() => exhausted.Player.Ends == 1);
                Check(exhausted.Remote.Played.SequenceEqual([from]) && exhausted.State.State.Playback.PendingSelection == null,
                    $"{style}: failed manual selection drains the deferred natural end once when the queue is exhausted");
            }

            using (var recovery = new Scenario())
            {
                var from = new Music(225, 40);
                var next = new Music(226);
                var outsideQueue = new Music(227, 41);
                recovery.State.CurrentPlayingList = [from, next];
                await BeginAsync(recovery, 0);
                var failedProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                recovery.Library.Probe = (_, token) => failedProbe.Task.WaitAsync(token);
                Task selection = recovery.Coordinator.PlayAsync(outsideQueue);
                await WaitAsync(() => recovery.Library.Probes.Contains(outsideQueue.SourceId));
                await ReportEndedAsync(recovery);
                failedProbe.SetResult(false);
                await selection;
                await WaitAsync(() => recovery.State.CurrentPlayingMusic == next && recovery.State.State.Playback.PendingSelection == null);
                Check(recovery.Player.Played.SequenceEqual([next]) && recovery.Remote.Played.SequenceEqual([from]),
                    $"{style}: failed explicit selection resumes deferred natural advance to the actual next entry exactly once");
            }

            foreach (var mode in new[] { WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RepeatOff, WinUIMusicPlayer.Utils.ToolUtils.PlayMode.SingleLoop,
                         WinUIMusicPlayer.Utils.ToolUtils.PlayMode.ListLoop, WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RandomLoop })
            {
                using var last = new Scenario();
                var only = new Music(224, 39) { IsRemoteCached = true };
                last.State.CurrentPlayingList = [only];
                last.State.CurrentPlayMode = mode;
                await BeginAsync(last, 0);
                long entry = last.State.State.Queue.CurrentEntryId;
                await ReportEndedAsync(last);
                bool stop = mode == WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RepeatOff;
                Check(last.Player.Ends == (stop ? 1 : 0) && last.Remote.Played.Count == (stop ? 1 : 2)
                      && last.State.State.Queue.CurrentEntryId == entry && last.Ipc.Queued.Count == 0,
                    $"{style} {mode}: last cached WebDAV entry follows stop or loop mode without a local transition");
            }
        }

        static async Task BeginAsync(Scenario test, int index)
        {
            test.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
            test.Lifecycle.TransitionTo(AppPhase.Initializing);
            test.Lifecycle.TransitionTo(AppPhase.Ready);
            // A recent local snapshot can still be in the state pipe when WebDAV begins.
            test.Ipc.Progress = new(1, 80, 1000, 100000, 0, true);
            await test.Coordinator.PlayAtAsync(index);
            test.State.IsPlaying = true;
        }

        static async Task CheckStoppedEndAsync(TrackTransitionStyle style)
        {
            using var stopped = new Scenario();
            var from = new Music(228);
            var next = new Music(229, 42) { IsRemoteCached = true };
            stopped.State.CurrentPlayingList = [from, next];
            await BeginAsync(stopped, 0);
            long staleResume = stopped.Coordinator.StopIntentVersion;
            stopped.Coordinator.StopPendingSelection();
            stopped.Player.MusicEnd();
            stopped.Coordinator.ResumeAfterStop(staleResume);
            await ReportEndedAsync(stopped);
            Check(stopped.Player.Played.SequenceEqual([from]) && stopped.Remote.Played.Count == 0,
                $"{style}: late EOF and stale resume acknowledgement cannot restart after explicit stop");

            stopped.Coordinator.ResumeAfterStop(stopped.Coordinator.StopIntentVersion);
            await ReportEndedAsync(stopped);
            Check(stopped.Remote.Played.SequenceEqual([next]), $"{style}: confirmed resume restores ordinary natural advance");

            stopped.Lifecycle.TryBeginExit(out _);
            stopped.State.CanStartPlayback = false;
            await ReportEndedAsync(stopped);
            Check(stopped.Player.Played.SequenceEqual([from]) && stopped.Remote.Played.SequenceEqual([next]),
                $"{style}: delayed WebDAV EOF during exit starts nothing");
        }

        static Task ReportEndedAsync(Scenario test)
        {
            test.State.IsPlaying = false;
            return test.Coordinator.AdvanceAfterEndAsync();
        }
    }

    private static async Task SongVersionsAsync()
    {
        using var test = new Scenario();
        var preferred = new Music(501, 10) { SongId = 70, EncodingKind = 1 };
        var fallback = new Music(502, 11) { SongId = 70, EncodingKind = 2 };
        preferred.SongPreferredMusicId = preferred.Id;
        fallback.SongPreferredMusicId = preferred.Id;
        test.State.LibrarySongs = [preferred, fallback];
        test.State.CurrentPlayingList = [fallback, fallback];
        var first = test.State.State.Queue.EntryIdAt(0);
        var second = test.State.State.Queue.EntryIdAt(1);
        test.Library.Probe = (source, _) => Task.FromResult(source.Id != 10);
        await test.Coordinator.PlayAsync(fallback, second);
        Check(test.Remote.Played.SequenceEqual([fallback]) && test.State.State.Queue.CurrentEntryId == second,
            "unavailable preferred version falls back within the same song and duplicate queue occurrence");
        Check(test.State.State.Queue.RequestedVersion(second) == 0, "automatic fallback does not pin queue occurrence");
        await test.Coordinator.PlayAsync(preferred, first, exactVersion: true);
        Check(test.Remote.Played.Count == 1 && test.State.State.Shell.InfoBarIsOpen, "explicit version failure never substitutes another file");
        Check(test.State.State.Queue.RequestedVersion(first) == preferred.Id && test.State.State.Queue.RequestedVersion(second) == 0,
            "explicit pin affects exactly one duplicate occurrence");
        test.State.State.Queue.ConfigureSource(11);
        await test.Coordinator.PlayAsync(fallback, second);
        Check(test.Library.Probes.Last() == 11, "source-scoped queue never probes a version from another source");
        test.State.State.Queue.ConfigurePlaylist([31, 32], [preferred.Id, 0], -1);
        Check(test.State.State.Queue.PlaylistEntry(second) == 32, "playlist occurrence identity survives duplicate versions");
        var ids = test.State.State.Queue.CaptureEntryIds();
        var order = test.State.State.Queue.CaptureOrderIds();
        var musicIds = test.State.State.Queue.CaptureMusicIds();
        var pins = test.State.State.Queue.CaptureRequestedVersions();
        var scopes = test.State.State.Queue.CaptureSourceScopes();
        var relations = test.State.State.Queue.CapturePlaylistEntries();
        test.State.State.Queue.RestoreEntries(ids, order, musicIds, second, pins, scopes, playlistEntries: relations);
        Check(test.State.State.Queue.CurrentEntryId == second && test.State.State.Queue.PlaylistEntry(second) == 32,
            "queue persistence restores duplicate identities and source relation");
        test.State.CurrentPlayingList.RemoveAt(0);
        Check(test.State.State.Queue.RequestedVersion(first) == 0 && test.State.State.Queue.PlaylistEntry(first) == 0,
            "removed occurrence releases its pin and playlist metadata");

        var queue = test.State.State.Queue;
        var missing = new Music(503) { SongId = 70, IsMissing = true };
        test.State.LibrarySongs = [missing, fallback];
        queue.Replace(new([missing]), 11);
        Check(PlaybackCommands.CanAttemptEntry(test.State, 0), "navigation can try a sibling when the display file is missing");
        queue.Replace(new([missing]), 0);
        Check(!PlaybackCommands.CanAttemptEntry(test.State, 0), "navigation does not leak out of the source filter");
        queue.Replace(new([missing]), -1, exactFiles: true);
        Check(!PlaybackCommands.CanAttemptEntry(test.State, 0), "an exact folder entry cannot borrow its playable sibling");
        queue.Mode = WinUIMusicPlayer.Utils.ToolUtils.PlayMode.RandomLoop;
        queue.Replace(new([fallback, fallback]));
        var shuffledIds = queue.CaptureEntryIds();
        queue.RestoreEntries(shuffledIds, [shuffledIds[1], shuffledIds[0]], queue.CaptureMusicIds(), 0, playlistEntries: [41, 42]);
        Check(queue.EntryIdAt(0) != queue.SequentialEntryIdAt(0) && queue.PlaylistEntry(queue.SequentialEntryIdAt(0)) == 41,
            "clicking the first playlist relation resolves the original occurrence in a reversed shuffle");

        string deletedPath = Path.GetTempFileName(), remainingPath = Path.GetTempFileName();
        try
        {
            using var deleted = new Scenario();
            var high = new Music(504) { SongId = 72, EncodingKind = 1, Path = deletedPath };
            var low = new Music(505) { SongId = 72, EncodingKind = 2, Path = remainingPath };
            deleted.State.LibrarySongs = [high, low];
            deleted.State.CurrentPlayingList = [high];
            File.Delete(deletedPath);
            await deleted.Coordinator.PlayAtAsync(0);
            Check(deleted.Player.Played.SequenceEqual([low]), "playback opens the surviving local version even before a scan sees the deletion");
            high.IsMissing = true;
            deleted.State.CurrentPlayingMusic = high;
            deleted.State.IsPlaying = false;
            deleted.Lifecycle.TransitionTo(AppPhase.WaitingForAgreement);
            deleted.Lifecycle.TransitionTo(AppPhase.Initializing);
            deleted.Lifecycle.TransitionTo(AppPhase.Ready);
            using var services = new ServiceCollection().AddSingleton(deleted.Player).AddSingleton(deleted.Remote)
                .AddSingleton(deleted.Coordinator).BuildServiceProvider();
            using var commands = new PlaybackCommands(deleted.Lifecycle, deleted.State, services);
            await commands.PlayCommand.ExecuteAsync(null);
            Check(deleted.Player.Played.SequenceEqual([low, low]), "Play on a paused missing automatic version reopens its available sibling");
        }
        finally { File.Delete(deletedPath); File.Delete(remainingPath); }

        using var opening = new Scenario();
        opening.State.LibrarySongs = [preferred, fallback];
        opening.State.CurrentPlayingList = [fallback];
        await opening.Coordinator.PlayAsync(fallback);
        opening.Remote.Fail(preferred);
        await WaitAsync(() => opening.Remote.Played.Count == 2);
        Check(opening.Remote.Played.SequenceEqual([preferred, fallback]), "decoder failure before confirmation tries the next version");
        opening.Remote.Confirm(fallback);
        opening.Remote.Fail(fallback);
        await Task.Delay(50);
        Check(opening.Remote.Played.Count == 2, "failure after confirmed playback does not switch versions mid-song");
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
        Player.Coordinator = Coordinator;
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
