using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.ViewModel;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services;

/// <summary>所有选曲与失败恢复入口；立即发布待播选择，开始播放时再提交曲目展示与统计。</summary>
public sealed partial class PlaybackCoordinator(AppViewModel state, BassPlayerCommandService player,
    PlaybackStatsService statistics, ApplicationTasks tasks, ShutdownCoordinator shutdown,
    ILogger<PlaybackCoordinator> logger, RemotePlaybackService remote, WebDavLibraryService library, IpcService ipc) : IDisposable
{
    private CancellationTokenSource? _presentation;
    private bool _disposed;
    private bool _registered;
    private long _selectionVersion;
    private long _playingVersion, _playingGeneration;
    private readonly AutoAdvanceGate _autoAdvance = new();
    private bool _deferredNaturalEnd;
    private long _stopIntentVersion, _resumeStopIntentVersion;
    private readonly HashSet<long> _failedEntries = [];
    private (Music Music, long Generation, bool Advance)? _deferredFailure;
    private (Music Music, long Entry, bool Exact, bool MayAdvance, bool Confirmed)? _activeIntent;
    private long _activeSelectionVersion, _activeLocalPlaybackId, _lastEndedEpoch;
    private (int Version, int Scope)? _manualQueueOptions;
    private readonly HashSet<int> _attemptedVersions = [];
    private readonly HashSet<int> _probedVersionSources = [];
    private Music? _intentMusic;
    private long _intentEntry;
    private int _intentScope = -1;
    private bool _intentExact, _intentMayAdvance, _versionConfirmed;
    private long _versionDeadline;
    /// <summary>Committed selection and its starting position, including audio already consumed during a handoff.</summary>
    public event Action<Music, long, CancellationToken>? TrackStarted;
    /// <summary>UI-thread notification after the engine confirms this playback occurrence.</summary>
    public event Action<Music, BassPlayerIpc.Shared.ProgressSnapshot>? PlaybackConfirmed;
    public event Action? SelectionChanged;

    /// <summary>连续点击上下首从正在探测的条目继续，不反复命中尚未开始播放的同一首。</summary>
    public int GetNavigationIndex()
    {
        return state.GetSelectedPlaybackIndex();
    }

    public Task PlayAtAsync(int index, int direction = 1, bool stopWhenUnavailable = false)
    {
        if (index < 0 || index >= state.CurrentPlayingList.Count) return Task.CompletedTask;
        if (direction is not (1 or -1)) throw new ArgumentOutOfRangeException(nameof(direction));
        _failedEntries.Clear();
        return RunSelectionAsync(token => PlayQueueCoreAsync(index, direction, stopWhenUnavailable, token),
            state.CurrentPlayingList[index], state.State.Queue.EntryIdAt(index));
    }

    internal Task PlayNextAsync()
    {
        if (_disposed || !state.CanStartPlayback) return Task.CompletedTask;
        int index = PlaybackCommands.FindCandidateIndex(state, GetNavigationIndex(), 1);
        if (index < 0) return Task.CompletedTask;
        if (_gaplessIpc == null) StartGapless();
        _failedEntries.Clear();
        long entryId = state.State.Queue.EntryIdAt(index);
        return RunSelectionAsync(token => PlayQueueCoreAsync(index, 1, false, token, manualEntryId: entryId),
            state.CurrentPlayingList[index], entryId, manualNext: true);
    }

    internal bool TryAcceptLocalEnd(BassPlayerIpc.Shared.PlaybackEnded ended)
    {
        if (!CanAdvanceAfterEnd || state.CurrentPlayingMusic is not { IsRemote: false }
            || ended.PlaybackId != _activeLocalPlaybackId || ended.Epoch <= _lastEndedEpoch
            || (ipc.TryGetProgressSnapshot(out var latest) && latest.Epoch > ended.Epoch)) return false;
        _lastEndedEpoch = ended.Epoch;
        return true;
    }

    internal async Task AdvanceAfterEndAsync()
    {
        if (!CanAdvanceAfterEnd || DeferEndForPendingSelection() || !_autoAdvance.Request()) return;
        do
        {
            if (!CanAdvanceAfterEnd)
            {
                _autoAdvance.Cancel();
                return;
            }
            if (DeferEndForPendingSelection()) continue;
            try { await PlayFollowingEntryAsync(); }
            catch (OperationCanceledException) when (!CanAdvanceAfterEnd) { }
            catch (Exception ex) { logger.LogError(ex, "曲目结束后切歌失败"); }
        }
        while (_autoAdvance.Complete());
    }

    private bool CanAdvanceAfterEnd => !_disposed && state.CanStartPlayback && _stopIntentVersion == _resumeStopIntentVersion;

    internal long StopIntentVersion => _stopIntentVersion;

    internal void ResumeAfterStop(long observedStopIntent)
    {
        if (observedStopIntent == _stopIntentVersion) _resumeStopIntentVersion = observedStopIntent;
    }

    internal void StopPendingSelection()
    {
        // A queued EOF belongs to the playback explicitly stopped by this intent.
        _stopIntentVersion++;
        _autoAdvance.DiscardPending();
        CancelPendingSelection();
        state.State.Playback.TransitionPresentation = null;
    }

    private bool DeferEndForPendingSelection()
    {
        if (state.State.Playback.PendingSelection == null || _activeSelectionVersion == _selectionVersion) return false;
        // 仅在探活或等待交接阶段推迟末尾处理；已提交的短曲仍可再次触发自然切歌。
        _deferredNaturalEnd = true;
        return true;
    }

    private Task PlayFollowingEntryAsync()
    {
        state.StopProgressTimer();
        switch (state.CurrentPlayMode)
        {
            case PlayMode.SingleLoop:
                if (state.CurrentPlayingMusic is { } current) return PlayAsync(current);
                break;
            case PlayMode.ListLoop:
            case PlayMode.RandomLoop:
                int nextIndex = PlaybackCommands.FindCandidateIndex(state, state.GetCurrentIndex(), 1,
                    allowSingleEntryReplay: true);
                if (nextIndex >= 0) return PlayAtAsync(nextIndex, stopWhenUnavailable: true);
                break;
        }
        player.MusicEnd();
        return Task.CompletedTask;
    }

    public Task PlayAsync(Music music, long entryId = 0, bool exactVersion = false)
        => SelectMusicAsync(music, entryId, resumeInterrupted: false, exactVersion);

    /// <summary>播放按钮恢复同一首断流歌曲；主动选曲仍从曲首开始。</summary>
    public Task ResumeCurrentAsync()
    {
        var music = state.CurrentPlayingMusic;
        if (music is not { IsRemote: true }) return Task.CompletedTask;
        return SelectMusicAsync(music, state.State.Queue.CurrentEntryId, resumeInterrupted: true,
            exactVersion: !music.IsMissing && !music.IsDetached);
    }

    private Task SelectMusicAsync(Music music, long entryId, bool resumeInterrupted, bool exactVersion)
    {
        if (_disposed || !state.CanStartPlayback || music is null) return Task.CompletedTask;
        _failedEntries.Clear();
        if (entryId == 0) entryId = state.State.Queue.EntryIdAt(state.State.Queue.IndexOf(music));
        if (exactVersion && !resumeInterrupted) state.State.Queue.SetRequestedVersion(entryId, music.Id);
        int requested = state.State.Queue.RequestedVersion(entryId);
        BeginVersionIntent(music, entryId, exactVersion || requested > 0, false);
        return RunSelectionAsync(async token =>
        {
            if (!await PlayVersionIntentAsync(token, resumeInterrupted)) ShowVersionFailure();
        }, music, entryId);
    }

    private Task RunSelectionAsync(Func<CancellationToken, Task> operation, Music music, long entryId, bool manualNext = false)
    {
        if (_disposed || !state.CanStartPlayback) return Task.CompletedTask;
        long version = ++_selectionVersion;
        BeginSelectionPreview(manualNext, music, entryId, version);
        BeginManualNext(manualNext, entryId, version);
        _presentation?.Cancel(); // 收到新意图即停止上一轮等待，不等 ApplicationTasks 的下一次调度。
        SetPending(new(music, entryId), manualNext);
        RegisterPlaybackLifecycle();
        return tasks.RunAsync(stoppingToken => RunSelectionCoreAsync(operation, stoppingToken, version));
    }

    private void RegisterPlaybackLifecycle()
    {
        if (_registered) return;
        _registered = true;
        shutdown.RegisterCleanup(Dispose);
        remote.Failed += OnRemoteFailed;
        remote.PlaybackStarted += OnRemotePlaybackStarted;
    }

    private async Task RunSelectionCoreAsync(Func<CancellationToken, Task> operation, CancellationToken stoppingToken, long version)
    {
        // The notification pipe may have committed the promoted decoder before this task was dispatched.
        if (_disposed || !state.CanStartPlayback || version != _selectionVersion || version == _activeSelectionVersion) return;
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var token = _presentation.Token;
        try { await operation(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "播放音乐失败"); }
        finally
        {
            if (version == _selectionVersion)
            {
                FinishSelectionPreview();
                FinishManualNext(version);
                SetPending(null);
                ReconcileSupersededHandoff();
                bool failedExact = _intentExact && _activeSelectionVersion != version;
                if (_activeSelectionVersion != version && _activeIntent is { } active && ReferenceEquals(state.CurrentPlayingMusic, active.Music))
                {
                    // 新选曲未开始时，当前会话仍由原有播放意图负责失败恢复。
                    BeginVersionIntent(active.Music, active.Entry, active.Exact, active.MayAdvance);
                    _attemptedVersions.Add(active.Music.Id);
                    _versionConfirmed = active.Confirmed;
                }
                _playingVersion = version;
                var failure = _deferredFailure;
                _deferredFailure = null;
                if (!failedExact && failure is { } deferred) OnRemoteFailed(deferred.Music, deferred.Generation, deferred.Advance);
                if (version == _selectionVersion && _deferredNaturalEnd)
                {
                    _deferredNaturalEnd = false;
                    if (!token.IsCancellationRequested) _ = AdvanceAfterEndAsync();
                }
                RefreshGaplessPlan();
            }
        }
    }

    public void CancelPendingSelection()
    {
        _deferredNaturalEnd = false;
        if (CancelManualNext()) return;
        CancelGaplessPlan();
        _playingVersion = ++_selectionVersion;
        _deferredFailure = null;
        FinishSelectionPreview();
        _presentation?.Cancel();
        SetPending(null);
        ReconcileSupersededHandoff();
    }

    private void SetPending(PlaybackSelection? selection, bool manualNext = false)
    {
        var queue = state.State.Queue;
        _manualQueueOptions = manualNext && selection != null
            ? (queue.RequestedVersion(selection.EntryId), queue.SourceScope(selection.EntryId)) : null;
        if (state.State.Playback.PendingSelection == selection) return;
        state.State.Playback.PendingSelection = selection;
        SelectionChanged?.Invoke();
    }

    private void OnRemoteFailed(Music music, long generation, bool advance)
    {
        // 已有新选曲时，只保留来源状态更新；旧失败不得取消新等待或夺回选中行。
        if (_disposed || !state.CanStartPlayback || !advance || generation != _playingGeneration ||
            !ReferenceEquals(state.CurrentPlayingMusic, music)) return;
        if (_playingVersion != _selectionVersion)
        {
            _deferredFailure = (music, generation, advance);
            return;
        }
        if (!_versionConfirmed && !_intentExact && _intentMusic?.SongId > 0)
        {
            _ = RunSelectionAsync(async token =>
            {
                if (!await PlayVersionIntentAsync(token)) AdvanceAfterVersionFailure();
            }, _intentMusic, _intentEntry);
            return;
        }
        if (_intentExact || !_intentMayAdvance) { ShowVersionFailure(); return; }
        AdvanceAfterVersionFailure();
    }

    private void AdvanceAfterVersionFailure()
    {
        if (!_intentMayAdvance) { ShowVersionFailure(); return; }
        _failedEntries.Add(state.State.Queue.CurrentEntryId);
        int count = state.CurrentPlayingList.Count;
        if (count == 0) { player.MusicEnd(); return; }
        int index = (state.GetCurrentIndex() + 1) % count;
        _ = RunSelectionAsync(token => PlayQueueCoreAsync(index, 1, true, token, preserveInterruptedProgress: true),
            state.CurrentPlayingList[index], state.State.Queue.EntryIdAt(index));
    }

    private async Task PlayQueueCoreAsync(int index, int direction, bool stopWhenUnavailable, CancellationToken token,
        bool preserveInterruptedProgress = false, long manualEntryId = 0)
    {
        var queue = state.State.Queue;
        bool manualNext = manualEntryId > 0;
        if (manualNext && queue.EntryIdAt(index) != manualEntryId) index = queue.IndexOfEntry(manualEntryId);
        var list = state.CurrentPlayingList;
        int count = list.Count;
        long originalEntry = queue.EntryIdAt(state.GetCurrentIndex());
        // 最多一遍队列；跨请求的来源重试期限由 library 负责，不能在此跳过同源的缓存歌曲。
        for (int attempted = 0; attempted < count; attempted++)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || !state.CanStartPlayback || (uint)index >= list.Count) return;
            var music = list[index];
            long entryId = queue.EntryIdAt(index);
            if (attempted > 0 && entryId == originalEntry) break;
            SetPending(new(music, entryId), manualNext);
            if (!_failedEntries.Contains(entryId))
            {
                BeginVersionIntent(music, entryId, state.State.Queue.RequestedVersion(entryId) > 0, true);
                var available = await ResolveVersionAsync(token);
                token.ThrowIfCancellationRequested();
                // The clicked occurrence can move while its version is being probed.
                if (manualNext)
                {
                    list = state.CurrentPlayingList;
                    if (queue.EntryIdAt(index) != entryId) index = queue.IndexOfEntry(entryId);
                }
                else if (!ReferenceEquals(list, state.CurrentPlayingList) || list.Count != count) return;
                if ((uint)index >= list.Count || queue.EntryIdAt(index) != entryId || !ReferenceEquals(list[index], music)) return;
                if (available is not null)
                {
                    if (manualNext && await TryManualNextAsync(available, entryId, token)) return;
                    token.ThrowIfCancellationRequested();
                    await PlayCoreAsync(available, entryId, token);
                    return;
                }
            }
            index = (index + direction + list.Count) % list.Count;
        }
        if (stopWhenUnavailable && !_disposed && state.CanStartPlayback && !token.IsCancellationRequested)
            player.MusicEnd(preserveInterruptedProgress);
    }

    private async Task<bool> IsAvailableAsync(Music music, CancellationToken token, bool retryImmediately = false)
    {
        if (!music.IsRemote) return music.IsPlayable && (music.SongId <= 0 || await Task.Run(() => File.Exists(music.Path), token));
        try
        {
            return await library.CanPlayAsync(music, retryImmediately, token);
        }
        catch (WebDavException) { return false; }
    }

    private void BeginVersionIntent(Music music, long entryId, bool exact, bool mayAdvance)
    {
        _intentMusic = music;
        _intentEntry = entryId;
        _intentScope = state.State.Queue.SourceScope(entryId);
        _intentExact = exact;
        _intentMayAdvance = mayAdvance;
        _versionConfirmed = false;
        _versionDeadline = Environment.TickCount64 + 30000;
        _attemptedVersions.Clear();
        _probedVersionSources.Clear();
    }

    private List<Music> OrderedVersions(Music music, int sourceScope)
    {
        List<Music> versions = [];
        if (music.SongId > 0)
            foreach (var version in state.FindVersions(music.SongId))
                if (version.SongId == music.SongId && SongVersionPolicy.InScope(version, sourceScope)) versions.Add(version);
        if (versions.Count == 0 && SongVersionPolicy.InScope(music, sourceScope)) versions.Add(music);
        return SongVersionPolicy.Order(versions, music.SongPreferredMusicId, forPlayback: true);
    }

    private async Task<Music?> ResolveVersionAsync(CancellationToken token)
    {
        if (_intentMusic is not { } requested) return null;
        List<Music> versions = _intentExact ? [requested] : OrderedVersions(requested, _intentScope);
        int pinned = state.State.Queue.RequestedVersion(_intentEntry);
        if (_intentExact && pinned > 0 && pinned != requested.Id)
        {
            versions.Clear();
            if (state.FindById(pinned) is { IsDetached: false } version) versions.Add(version);
        }
        foreach (var version in versions)
        {
            token.ThrowIfCancellationRequested();
            if (Environment.TickCount64 >= _versionDeadline) return null;
            if (!SongVersionPolicy.InScope(version, _intentScope) || !_attemptedVersions.Add(version.Id)) continue;
            PresentManualCandidate(version, _intentEntry);
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
            probe.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1, _versionDeadline - Environment.TickCount64)));
            bool retryImmediately = !_intentMayAdvance && _probedVersionSources.Add(version.SourceId);
            try { if (await IsAvailableAsync(version, probe.Token, retryImmediately)) return version; }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
        }
        return null;
    }

    private async Task<bool> PlayVersionIntentAsync(CancellationToken token, bool resumeInterrupted = false)
    {
        var version = await ResolveVersionAsync(token);
        if (version is null) return false;
        await PlayCoreAsync(version, _intentEntry, token, resumeInterrupted);
        return true;
    }

    private void ShowVersionFailure()
    {
        state.State.Shell.InfoBarTitle = Utils.ToolUtils.GetString("Error");
        state.State.Shell.InfoBarMessage = Utils.ToolUtils.GetString("SongVersionsUnavailable");
        state.State.Shell.InfoBarIsOpen = true;
    }

    private void OnRemotePlaybackStarted(Music music, BassPlayerIpc.Shared.ProgressSnapshot progress, bool resumed)
        => ConfirmVersion(music);

    private void ConfirmVersion(Music music)
    {
        if (ReferenceEquals(state.CurrentPlayingMusic, music) && _activeIntent is { } active)
        {
            _activeIntent = active with { Confirmed = true, MayAdvance = active.MayAdvance || !active.Exact };
            if (_playingVersion == _selectionVersion)
            {
                _versionConfirmed = true;
                _intentExact = active.Exact;
                _intentMayAdvance |= !_intentExact;
            }
        }
    }

    private async Task PlayCoreAsync(Music music, long entryId, CancellationToken token, bool resumeInterrupted = false)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !state.CanStartPlayback) return;
        bool exact = _intentExact && (!resumeInterrupted || state.State.Queue.RequestedVersion(entryId) > 0);
        bool mayAdvance = _intentMayAdvance;
        long previousEpoch = 0;
        long remoteGeneration = music.IsRemote ? remote.BeginSelection(music, resumeInterrupted) : 0;
        _playingGeneration = remoteGeneration;
        _playingVersion = _selectionVersion;
        _deferredFailure = null;
        if (!music.IsRemote)
        {
            // 服务同步登记停止代次，后台执行可能同步 Flush(true) 的缓存收尾。
            await remote.StopAsync().WaitAsync(token);
            token.ThrowIfCancellationRequested();
        }
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            if (_disposed || !state.CanStartPlayback || token.IsCancellationRequested) return;
            if (!music.IsRemote)
            {
                previousEpoch = ipc.TryGetProgressSnapshot(out var before) ? before.Epoch : 0;
                long playbackId = ++_nextToken;
                player.PlayMusic(music, playbackId);
                _activeLocalPlaybackId = playbackId;
                _lastEndedEpoch = 0;
            }
            _supersededHandoff = null;
            _deferredNaturalEnd = false;
            _resumeStopIntentVersion = _stopIntentVersion;
            state.State.Queue.SelectEntry(entryId, music);
            _activeIntent = (music, entryId, exact, mayAdvance, false);
            _activeSelectionVersion = _selectionVersion;
            bool lyricsAlreadyPresented = IsPresenting(music, entryId);
            state.CurrentPlayingMusic = music;
            FinishTrackPresentation(music, entryId, previousEpoch, lyricsAlreadyPresented);
            state.RemotePlaybackStatus = music.IsRemote ? Utils.ToolUtils.GetString("WebDavOpening") : "";
            if (music.IsRemote) { state.IsPlaying = false; state.StopProgressTimer(); }
            try { if (music.IsRemote) statistics.FlushSession(); else statistics.StartSession(music); }
            catch (Exception ex) { logger.LogError(ex, "记录播放统计会话失败"); }
            if (!lyricsAlreadyPresented)
            {
                state.UILyrics = [];
                state.LoadLyricsToUI(music);
            }
            state.UpdateProgressTimerUI();
            long positionMs = resumeInterrupted ? remote.GetProgress()?.CurrentMs ?? 0 : 0;
            TrackStarted?.Invoke(music, positionMs, token);
            StartGapless();
        });
        // 远程准备包含 PasswordVault 和缓存文件前置工作；只有展示发布回 UI 线程。
        if (music.IsRemote && !token.IsCancellationRequested)
            await Task.Run(() => remote.PlayAsync(music, remoteGeneration, token), token);
        else if (!music.IsRemote)
            _ = tasks.RunAsync(stopping => ConfirmLocalStartAsync(music, previousEpoch, token, stopping));
    }

    private async Task ConfirmLocalStartAsync(Music music, long previousEpoch, CancellationToken selection, CancellationToken stopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(selection, stopping);
        var token = linked.Token;
        try
        {
            // Enqueueing a command alone does not prove the decoder started. Seek and
            // lyric reload never enter this path. Keep confirmation outside selection arbitration.
            long deadline = Math.Min(Environment.TickCount64 + 10000, _versionDeadline);
            while (Environment.TickCount64 < deadline)
            {
                token.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(state.CurrentPlayingMusic, music)) return;
                if (ipc.TryGetProgressSnapshot(out var progress) && progress.Playing && progress.Epoch != previousEpoch)
                {
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        if (!_disposed && state.CanPublishState && !token.IsCancellationRequested
                            && ReferenceEquals(state.CurrentPlayingMusic, music))
                        {
                            ClearConfirmedPresentation(progress);
                            ConfirmVersion(music);
                            PlaybackConfirmed?.Invoke(music, progress);
                        }
                    });
                    await statistics.RecordPlaybackStartAsync(music);
                    return;
                }
                await Task.Delay(50, token);
            }
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (_disposed || token.IsCancellationRequested || !ReferenceEquals(state.CurrentPlayingMusic, music)) return;
                if (state.State.Playback.TransitionPresentation is { IsCommitted: true, Token: 0 })
                    state.State.Playback.TransitionPresentation = null;
                if (_intentExact || _intentMusic?.SongId <= 0) { ShowVersionFailure(); return; }
                _ = RunSelectionAsync(async retry =>
                {
                    if (!await PlayVersionIntentAsync(retry)) AdvanceAfterVersionFailure();
                }, _intentMusic!, _intentEntry);
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "确认播放开始失败"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopGapless();
        _disposed = true;
        _autoAdvance.Cancel();
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = null;
        remote.Failed -= OnRemoteFailed;
        remote.PlaybackStarted -= OnRemotePlaybackStarted;
        state.State.Playback.PendingSelection = null;
        SelectionChanged = null;
        TrackStarted = null;
        PlaybackConfirmed = null;
    }
}
