using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services;

public sealed partial class PlaybackCoordinator
{
    // SelectionVersion rejects an old user intent; Epoch rejects an old engine timeline;
    // Token identifies this exact plan, including duplicate queue entries.
    private sealed record NextPlan(long Token, long SelectionVersion, Music From, Music Next,
        long EntryId, long Epoch, AudioInput Input, TrackTransitionStyle Style, int CrossfadeMilliseconds, bool PreserveAlbumBoundary)
    {
        public GaplessRequest Request => new(Epoch, Token, Input.Path, PreserveAlbumBoundary, Input.SampleRange);
    }

    internal static AudioInput GetAudioInput(Music music) => new(music.Path,
        music.IsCueTrack ? new AudioSampleRange(music.StartSample, music.EndSample, music.SampleRate) : null);
    private NextPlan? _nextPlan;
    private NextPlan? _cancellingPlan;
    private TaskCompletionSource? _gaplessCancellationSettled;
    private (long Epoch, long PositionMs) _cancellingTransition;
    private (NextPlan Plan, long Epoch, long PositionMs)? _supersededHandoff;
    private IpcService? _gaplessIpc;
    private DispatcherQueueTimer? _gaplessTimer;
    private long _nextToken, _lastAcceptedToken, _retryAfter, _outputGeneration;
    private bool _planConfirmed, _planSending, _acceptingGapless;
    private bool _cancelSending;
    private long _cancelRetryAfter;
    private bool _candidateDirty = true;
    private bool _candidatePreserveAlbumBoundary;
    private long _candidateQueueVersion = -1, _candidateCurrentEntry, _candidateEntry;
    private PlayMode _candidateMode;
    private Music? _candidateFrom, _candidateNext;
    private long _manualNextVersion;
    private long _previewNextVersion;

    internal bool IsManualNextPending => _manualNextVersion != 0 && _manualNextVersion == _selectionVersion;

    private static bool AllowsManualNext(DspSettings settings) => settings.GaplessPlayback
        && settings.TransitionStyle is TrackTransitionStyle.Crossfade or TrackTransitionStyle.Smart;

    private bool HasManualPreview => _previewNextVersion != 0 && _previewNextVersion == _selectionVersion;

    private void BeginSelectionPreview(bool manualNext, Music requested, long entryId, long version)
    {
        _previewNextVersion = manualNext && state.CurrentPlayingMusic is { IsRemote: false } ? version : 0;
        if (!HasManualPreview)
        {
            ClearUncommittedPresentation();
            return;
        }

        int exact = state.State.Queue.RequestedVersion(entryId);
        var candidate = _nextPlan is { } prepared && prepared.EntryId == entryId ? prepared.Next
            : exact > 0 && exact != requested.Id ? state.FindById(exact) ?? requested : requested;
        PresentManualCandidate(candidate, entryId);
    }

    private void PresentManualCandidate(Music candidate, long entryId)
    {
        if (!HasManualPreview) return;
        // Network probing retains the existing display, including for a cached WebDAV file.
        if (candidate.IsRemote || !candidate.IsPlayable)
        {
            ClearUncommittedPresentation();
            return;
        }
        if (IsPresenting(candidate, entryId)) return;
        state.State.Playback.TransitionPresentation = new(candidate, entryId, DurationMs: (long)candidate.Duration.TotalMilliseconds);
    }

    private bool IsPresenting(Music music, long entryId) => state.State.Playback.TransitionPresentation is { IsCommitted: false } shown
        && shown.EntryId == entryId && ReferenceEquals(shown.Music, music);

    private void FinishSelectionPreview()
    {
        _previewNextVersion = 0;
        ClearUncommittedPresentation();
    }

    private void ClearUncommittedPresentation()
    {
        if (state.State.Playback.TransitionPresentation is { IsCommitted: false })
            state.State.Playback.TransitionPresentation = null;
    }

    private void FinishTrackPresentation(Music music, long entryId, long previousEpoch, bool hadPreview)
    {
        _previewNextVersion = 0;
        // An ordinary manual fallback still waits for a new engine epoch before it can show live progress.
        state.State.Playback.TransitionPresentation = (hadPreview || music.IsCueTrack) && !music.IsRemote
            ? new PlaybackTransitionPresentation(music, entryId, Epoch: previousEpoch,
                DurationMs: (long)music.Duration.TotalMilliseconds, IsCommitted: true)
            : null;
    }

    private void ClearConfirmedPresentation(ProgressSnapshot progress)
    {
        if (state.State.Playback.TransitionPresentation is not { IsCommitted: true } shown) return;
        bool synchronized = shown.Token > 0 ? progress.GaplessToken == shown.Token
            : progress.Playing && progress.Epoch > 0 && progress.Epoch != shown.Epoch
                && (!shown.Music.IsCueTrack || progress.PlaybackId == _activeLocalPlaybackId);
        if (!synchronized) return;
        state.State.Playback.TransitionPresentation = null;
        UpdateGaplessTimer();
    }

    private void BeginManualNext(bool requested, long entryId, long version)
    {
        bool replacingManual = _manualNextVersion != 0;
        _manualNextVersion = 0;
        var settings = AppSettings.Dsp;
        if (!requested || !AllowsManualNext(settings) || !state.IsPlaying || state.CurrentPlayingMusic is not { IsRemote: false })
        {
            CancelGaplessPlan();
            UpdateGaplessTimer();
            return;
        }

        _manualNextVersion = version;
        UpdateGaplessTimer();
        if (!replacingManual && _nextPlan is { } prepared && prepared.EntryId == entryId)
        {
            // Keep the natural decoder and token when Next targets the same queue occurrence.
            _nextPlan = prepared with { SelectionVersion = version };
            return;
        }
        CancelGaplessPlan();
    }

    private void FinishManualNext(long version)
    {
        if (_manualNextVersion != version) return;
        _manualNextVersion = 0;
        CancelGaplessPlan();
        UpdateGaplessTimer();
    }

    // Pause, stop and seek invalidate the pending click, while an already audible handoff still owns the track.
    internal bool CancelManualNext()
    {
        if (_disposed || (!IsManualNextPending && !HasManualPreview && !_manualQueueOptions.HasValue)) return false;
        _playingVersion = ++_selectionVersion;
        _manualNextVersion = 0;
        _deferredFailure = null;
        _deferredNaturalEnd = false;
        FinishSelectionPreview();
        CancelGaplessPlan();
        _presentation?.Cancel();
        SetPending(null);
        ReconcileSupersededHandoff();
        UpdateGaplessTimer();
        return true;
    }

    private async Task<bool> TryManualNextAsync(Music next, long entryId, CancellationToken token)
    {
        if (!IsManualNextPending) return false;
        if (!await WaitForGaplessCancellationAsync(token)) return true;
        var settings = AppSettings.Dsp;
        if (!state.IsPlaying || !AllowsManualNext(settings) || state.CurrentPlayingMusic is not { IsRemote: false } current
            || next.IsRemote || string.IsNullOrEmpty(next.Path) || !ipc.TryGetProgressSnapshot(out var progress)
            || !progress.Playing || progress.Epoch <= 0)
        {
            CancelGaplessPlan();
            return !await WaitForGaplessCancellationAsync(token);
        }

        var plan = _nextPlan;
        var input = GetAudioInput(next);
        if (plan != null && (plan.EntryId != entryId || plan.Next != next || plan.Input != input
            || (plan.Epoch != progress.Epoch && plan.Token != progress.GaplessToken)))
        {
            CancelGaplessPlan();
            if (!await WaitForGaplessCancellationAsync(token)) return true;
            plan = null;
        }
        token.ThrowIfCancellationRequested();
        if (plan is null)
        {
            long epoch = Math.Max(progress.Epoch, _supersededHandoff?.Epoch ?? 0);
            plan = new(++_nextToken, _selectionVersion, current, next, entryId, epoch, input,
                settings.TransitionStyle, settings.CrossfadeMilliseconds, PreserveAlbumBoundary(current, next));
            _nextPlan = plan;
        }

        var request = plan.Request;
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var statusTimer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        long outputGeneration = _outputGeneration;
        long deadline = Environment.TickCount64 + 1500;
        bool submitted = false;
        bool started = false;
        while (Environment.TickCount64 < deadline && state.IsPlaying)
        {
            token.ThrowIfCancellationRequested();
            if (!next.IsPlayable || plan.Input != GetAudioInput(next))
            {
                CancelManualNext();
                return true;
            }
            if (ipc.TryGetProgressSnapshot(out progress) && progress.GaplessToken == plan.Token)
            {
                AcceptCommittedGapless(plan, progress.Epoch, progress.CurrentMs);
                return true;
            }
            // Settings or output changes can end the overlap attempt without discarding the explicit Next.
            if (outputGeneration != _outputGeneration || (!started && !AllowsManualNext(AppSettings.Dsp))) break;
            TransitionNowStatus status;
            requestLifetime.CancelAfter((int)Math.Clamp(deadline - Environment.TickCount64, 1, 1000));
            try
            {
                status = submitted
                    ? await ipc.PollTransitionNowAsync(plan.Epoch, plan.Token, requestLifetime.Token)
                    : await ipc.TransitionNowAsync(request, requestLifetime.Token);
                submitted = true;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "手动下一首的交接状态未确认");
                break;
            }
            finally { requestLifetime.CancelAfter(Timeout.Infinite); }
            token.ThrowIfCancellationRequested();
            if (status == TransitionNowStatus.Committed)
            {
                await ConfirmManualCommitAsync(plan, token);
                return true;
            }
            if (status == TransitionNowStatus.Unavailable) break;
            if (status == TransitionNowStatus.Started && !started)
            {
                started = true;
                // Completion uses GaplessTransition or progress. Poll less often once the mix is audible.
                statusTimer.Period = TimeSpan.FromMilliseconds(500);
                int durationMs = Math.Max(settings.CrossfadeMilliseconds, AppSettings.Dsp.CrossfadeMilliseconds);
                deadline = Environment.TickCount64 + Math.Clamp(durationMs, DspSettings.MinCrossfadeMilliseconds, DspSettings.MaxCrossfadeMilliseconds) + 2000;
            }
            await statusTimer.WaitForNextTickAsync(token);
        }

        // The cancel reply is the ordering barrier. A transition that committed before it wins over fallback Play.
        CancelGaplessPlan();
        return !await WaitForGaplessCancellationAsync(token);
    }

    private async Task ConfirmManualCommitAsync(NextPlan plan, CancellationToken token)
    {
        // The status is authoritative, but the state pipe carries the actual incoming position.
        long deadline = Environment.TickCount64 + 250;
        do
        {
            if (ipc.TryGetProgressSnapshot(out var progress) && progress.GaplessToken == plan.Token)
            {
                AcceptCommittedGapless(plan, progress.Epoch, progress.CurrentMs);
                return;
            }
            await Task.Delay(25, token);
        }
        while (Environment.TickCount64 < deadline);
        token.ThrowIfCancellationRequested();
        AcceptCommittedGapless(plan, 0, 0);
    }

    private async Task<bool> WaitForGaplessCancellationAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_cancellingPlan != null && state.CanStartPlayback)
        {
            var settled = _gaplessCancellationSettled ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            await settled.Task.WaitAsync(token);
        }
        token.ThrowIfCancellationRequested();
        // Without a confirmed barrier, the decoder may have already taken over. Avoid issuing another Play.
        return !_disposed && _cancellingPlan == null && state.CanStartPlayback;
    }

    internal void StartGapless()
    {
        if (_disposed) return;
        if (_gaplessIpc == null)
        {
            RegisterPlaybackLifecycle();
            _gaplessIpc = ipc;
            _gaplessIpc.NotificationReceived += GaplessNotification;
            _gaplessIpc.DspStateChanged += GaplessOutputChanged;
            _gaplessIpc.TransitionProgressChanged += GaplessTransitionProgressChanged;
            _outputGeneration = _gaplessIpc.CurrentDspState?.State.OutputGeneration ?? 0;
            state.PropertyChanged += GaplessStateChanged;
            state.State.Queue.PropertyChanged += GaplessQueueChanged;
            AppSettings.PlaybackTransitionsChanged += GaplessPreferencesChanged;
            _gaplessTimer = App.MainWindow.DispatcherQueue.CreateTimer();
            _gaplessTimer.Interval = TimeSpan.FromMilliseconds(500);
            _gaplessTimer.Tick += GaplessTick;
        }
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void UpdateGaplessTimer()
    {
        if (!_disposed && state.CanStartPlayback && (_cancellingPlan != null || state.State.Playback.TransitionPresentation is { IsCommitted: true }
            || !IsManualNextPending && AppSettings.Dsp.GaplessPlayback && state.IsPlaying && state.CurrentPlayingMusic is { IsRemote: false }
                && state.CurrentPlayMode != PlayMode.RepeatOff))
            _gaplessTimer?.Start();
        else _gaplessTimer?.Stop();
    }

    // SettingsCoordinator sends the DSP snapshot during this event. Queue changes follow it on the ordered command pipe.
    private void GaplessPreferencesChanged(object? sender, EventArgs e)
        => App.MainWindow.DispatcherQueue.TryEnqueue(RefreshGaplessPreferences);

    private void RefreshGaplessPreferences()
    {
        if (_disposed) return;
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void GaplessOutputChanged()
    {
        long generation = ipc.CurrentDspState?.State.OutputGeneration ?? 0;
        if (generation != Volatile.Read(ref _outputGeneration))
            App.MainWindow.DispatcherQueue.TryEnqueue(RefreshGaplessOutput);
    }

    private void RefreshGaplessOutput()
    {
        if (_disposed) return;
        long generation = _gaplessIpc?.CurrentDspState?.State.OutputGeneration ?? 0;
        if (generation == _outputGeneration) return;
        Volatile.Write(ref _outputGeneration, generation);
        // An output rebuild can invalidate preparation without changing the timeline epoch.
        // The old confirmation cannot satisfy this new output, even if its reply arrives late.
        if (!IsManualNextPending) CancelGaplessPlan();
        RefreshGaplessPlan();
    }

    private void GaplessStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_acceptingGapless) return;
        if (e.PropertyName is nameof(state.IsPlaying) or nameof(state.CurrentPlayingMusic))
        {
            UpdateGaplessTimer();
            if (IsManualNextPending && e.PropertyName == nameof(state.IsPlaying)) return;
            CancelGaplessPlan();
            RefreshGaplessPlan();
        }
        // Queue mode/list notifications precede rebuilding the order. Use its committed Version.
    }

    private void GaplessQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(state.State.Queue.Version)) return;
        _candidateDirty = true;
        if (_manualQueueOptions is { } options && state.State.Playback.PendingSelection is { } pending)
        {
            var queue = state.State.Queue;
            int index = queue.IndexOfEntry(pending.EntryId);
            if (index < 0 || !ReferenceEquals(state.CurrentPlayingList[index], pending.Music)
                || options != (queue.RequestedVersion(pending.EntryId), queue.SourceScope(pending.EntryId))
                || !PlaybackCommands.CanAttemptEntry(state, index)) CancelManualNext();
        }
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void CandidateMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Music.Path) or nameof(Music.IsPlayable) or nameof(Music.Album)
            or nameof(Music.AlbumArtist) or nameof(Music.Author) or nameof(Music.DiskNumber) or nameof(Music.TrackNumber)
            or nameof(Music.IsCueTrack) or nameof(Music.CuePath) or nameof(Music.StartSample)
            or nameof(Music.EndSample) or nameof(Music.SampleRate))) return;
        var dispatcher = App.MainWindow.DispatcherQueue;
        if (dispatcher.HasThreadAccess) RefreshCandidateMusic(sender);
        else dispatcher.TryEnqueue(() => RefreshCandidateMusic(sender));
    }

    private void RefreshCandidateMusic(object? sender)
    {
        if (_disposed || sender != _candidateFrom && sender != _candidateNext) return;
        _candidateDirty = true;
        RefreshGaplessPlan();
    }

    private void UnobserveCandidates()
    {
        if (_candidateFrom != null) _candidateFrom.PropertyChanged -= CandidateMusicChanged;
        if (_candidateNext != null && _candidateNext != _candidateFrom)
            _candidateNext.PropertyChanged -= CandidateMusicChanged;
    }

    private void ResolveCandidate(Music current)
    {
        var queue = state.State.Queue;
        if (!_candidateDirty && _candidateQueueVersion == queue.Version && _candidateFrom == current
            && _candidateCurrentEntry == queue.CurrentEntryId && _candidateMode == state.CurrentPlayMode) return;
        UnobserveCandidates();
        _candidateDirty = false;
        _candidateQueueVersion = queue.Version;
        _candidateCurrentEntry = queue.CurrentEntryId;
        _candidateMode = state.CurrentPlayMode;
        _candidateFrom = current;
        _candidateNext = null;
        _candidateEntry = 0;
        _candidatePreserveAlbumBoundary = false;
        if (_candidateMode == PlayMode.SingleLoop)
        {
            _candidateNext = current;
            _candidateEntry = queue.CurrentEntryId;
        }
        else
        {
            int index = PlaybackCommands.FindCandidateIndex(state, state.GetCurrentIndex(), 1,
                allowSingleEntryReplay: true);
            if (index >= 0)
            {
                _candidateNext = state.CurrentPlayingList[index];
                _candidateEntry = queue.EntryIdAt(index);
            }
        }
        if (_candidateNext is not null)
        {
            int exact = queue.RequestedVersion(_candidateEntry);
            if (exact > 0)
            {
                if (_candidateNext.Id != exact) _candidateNext = state.FindById(exact);
                if (_candidateNext is not null && !SongVersionPolicy.InScope(_candidateNext, queue.SourceScope(_candidateEntry))) _candidateNext = null;
            }
            else if (_candidateNext.SongId > 0)
            {
                var versions = OrderedVersions(_candidateNext, queue.SourceScope(_candidateEntry));
                _candidateNext = versions.Find(static version => version.IsRemote || version.IsPlayable);
            }
        }
        current.PropertyChanged += CandidateMusicChanged;
        if (_candidateNext is { } next)
        {
            if (next != current) next.PropertyChanged += CandidateMusicChanged;
            _candidatePreserveAlbumBoundary = PreserveAlbumBoundary(current, next);
        }
    }

    private static bool PreserveAlbumBoundary(Music current, Music next)
    {
        // 同一首歌的循环（含重复出现项或文件版本）保留原始边界。
        if (current == next || current.Id > 0 && current.Id == next.Id
            || current.SongId > 0 && current.SongId == next.SongId)
            return true;
        if (!string.IsNullOrWhiteSpace(current.Path) && string.Equals(current.Path, next.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (!current.IsCueTrack && !next.IsCueTrack) return true;
            return current.IsCueTrack && next.IsCueTrack
                && string.Equals(current.CuePath, next.CuePath, StringComparison.OrdinalIgnoreCase)
                && Math.Max(1, current.DiskNumber) == Math.Max(1, next.DiskNumber)
                && GetAudioInput(current).IsImmediatelyFollowedBy(GetAudioInput(next));
        }

        // 缺省碟号是 0，按单碟的碟 1 处理；曲号必须明确且连续。
        if (current.TrackNumber <= 0 || (long)current.TrackNumber + 1 != next.TrackNumber
            || current.DiskNumber < 0 || next.DiskNumber < 0 || Math.Max(1, current.DiskNumber) != Math.Max(1, next.DiskNumber))
            return false;

        var album = current.Album.AsSpan().Trim();
        if (!SongVersionPolicy.IsKnownTag(album) || !album.Equals(next.Album.AsSpan().Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        var artist = GetAlbumArtist(current);
        return SongVersionPolicy.IsKnownTag(artist) && artist.Equals(GetAlbumArtist(next), StringComparison.OrdinalIgnoreCase);

        static ReadOnlySpan<char> GetAlbumArtist(Music music)
        {
            var albumArtist = music.AlbumArtist.AsSpan().Trim();
            return SongVersionPolicy.IsKnownTag(albumArtist) ? albumArtist : music.Author.AsSpan().Trim();
        }
    }

    private void GaplessTick(DispatcherQueueTimer sender, object args) => RefreshGaplessPlan();

    private void RefreshGaplessPlan()
    {
        if (_disposed || !state.CanStartPlayback || _gaplessIpc == null || _acceptingGapless) return;
        if (state.State.Playback.TransitionPresentation is { IsCommitted: true }
            && _gaplessIpc.TryGetProgressSnapshot(out var synchronized)) ClearConfirmedPresentation(synchronized);
        // Keep the old identity until its ordered cancellation reply: the render thread
        // may already have committed it even when its transition notification is late.
        if (_cancellingPlan is { } cancelling)
        {
            if (!_cancelSending && Environment.TickCount64 >= _cancelRetryAfter)
                _ = CancelGaplessPlanAsync(cancelling);
            return;
        }
        if (IsManualNextPending) return;
        var settings = AppSettings.Dsp;
        if (!state.IsPlaying || !settings.GaplessPlayback || state.State.Playback.PendingSelection != null
            || state.CurrentPlayingMusic is not { IsRemote: false, IsPlayable: true } current
            || state.CurrentPlayMode == PlayMode.RepeatOff)
        {
            CancelGaplessPlan();
            return;
        }
        bool hasProgress = _gaplessIpc.TryGetProgressSnapshot(out var progress) && progress.Epoch > 0;
        var plan = _nextPlan;
        if (hasProgress && plan is not null && progress.GaplessToken == plan.Token)
        {
            AcceptGapless(plan.Token, progress.Epoch, progress.CurrentMs);
            return;
        }
        ResolveCandidate(current);
        if (_candidateNext is not { IsRemote: false, IsPlayable: true } next || string.IsNullOrEmpty(next.Path))
        {
            CancelGaplessPlan();
            return;
        }
        // 保留已入队计划的专辑信息，播放端在设置命令与替换请求之间也能正确启用智能模式。
        bool preserveAlbumBoundary = _candidatePreserveAlbumBoundary;
        var input = GetAudioInput(next);
        if (plan is not null && ((hasProgress && plan.Epoch != progress.Epoch) || plan.EntryId != _candidateEntry
            || plan.From != current || plan.Next != next || plan.SelectionVersion != _selectionVersion || plan.Input != input
            || plan.Style != settings.TransitionStyle || plan.CrossfadeMilliseconds != settings.CrossfadeMilliseconds
            || plan.PreserveAlbumBoundary != preserveAlbumBoundary))
        {
            CancelGaplessPlan();
            return;
        }
        if (!hasProgress || !progress.Playing) return;
        if (plan is null)
        {
            plan = new(++_nextToken, _selectionVersion, current, next, _candidateEntry, progress.Epoch, input,
                settings.TransitionStyle, settings.CrossfadeMilliseconds, preserveAlbumBoundary);
            _nextPlan = plan;
            _planConfirmed = false;
            _planSending = false;
            _retryAfter = 0;
        }
        if (_planConfirmed || _planSending || Environment.TickCount64 < _retryAfter) return;
        _planSending = true;
        _ = SendGaplessPlanAsync(plan);
    }

    private async Task SendGaplessPlanAsync(NextPlan plan)
    {
        bool accepted = false;
        try { accepted = await _gaplessIpc!.QueueNextAsync(plan.Request); }
        catch (Exception ex) { logger.LogWarning(ex, "无法提交下一曲预载计划，保留普通切歌"); }
        if (_disposed || !ReferenceEquals(_nextPlan, plan)) return;
        _planSending = false;
        _planConfirmed = accepted;
        _retryAfter = Environment.TickCount64 + 1000;
    }

    private void CancelGaplessPlan()
    {
        if (_nextPlan is not { } plan) return;
        RollBackPlanPresentation(plan.Token);
        _nextPlan = null;
        _planConfirmed = false;
        _planSending = false;
        _cancellingPlan = plan;
        _cancellingTransition = default;
        UpdateGaplessTimer();
        _ = CancelGaplessPlanAsync(plan);
    }

    private async Task CancelGaplessPlanAsync(NextPlan plan)
    {
        _cancelSending = true;
        bool confirmed = false;
        (long Token, long Epoch) committed = default;
        try
        {
            committed = await _gaplessIpc!.CancelQueuedNextAsync();
            confirmed = true;
        }
        catch (Exception ex) { logger.LogWarning(ex, "取消下一曲预载失败"); }
        // The caller may still be establishing a selection, pause or shutdown intent.
        // Even a synchronously completed IPC task must not reenter it with a new plan.
        await Task.Yield();
        if (_disposed || !ReferenceEquals(_cancellingPlan, plan)) return;
        _cancelSending = false;
        _cancelRetryAfter = Environment.TickCount64 + 1000;
        if (!confirmed) return;
        var transition = _cancellingTransition;
        var settled = _gaplessCancellationSettled;
        _gaplessCancellationSettled = null;
        _cancellingPlan = null;
        _cancellingTransition = default;
        UpdateGaplessTimer();
        try
        {
            if (committed.Token == plan.Token)
                ReconcileCancelledPlan(plan, transition.Epoch > 0 ? transition.Epoch : committed.Epoch, transition.PositionMs);
            ReconcileSupersededHandoff();
            RefreshGaplessPlan();
        }
        catch (Exception ex) { logger.LogError(ex, "处理下一曲取消回执失败"); }
        finally { settled?.TrySetResult(); }
    }

    private void ReconcileCancelledPlan(NextPlan plan, long epoch, long positionMs)
    {
        if (plan.SelectionVersion == _selectionVersion)
        {
            AcceptCommittedGapless(plan, epoch, positionMs);
            return;
        }
        // The cancel reply proves this handoff was audible. A newer selection can still replace it.
        if (plan.Token <= _lastAcceptedToken || plan.SelectionVersion < _activeSelectionVersion
            || state.CurrentPlayingMusic != plan.From) return;
        _supersededHandoff = (plan, epoch, positionMs);
    }

    private void ReconcileSupersededHandoff()
    {
        if (_supersededHandoff is not { } confirmed || _cancellingPlan != null
            || state.State.Playback.PendingSelection != null) return;
        _supersededHandoff = null;
        if (confirmed.Plan.SelectionVersion < _activeSelectionVersion) return;
        AcceptCommittedGapless(confirmed.Plan with { SelectionVersion = _selectionVersion }, confirmed.Epoch, confirmed.PositionMs);
    }

    private void GaplessNotification(MessageTypeId type, ReadOnlyMemory<byte> payload)
    {
        if (type != MessageTypeId.GaplessTransition || payload.Length is not (16 or 24)) return;
        long token = BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
        long epoch = BinaryPrimitives.ReadInt64LittleEndian(payload.Span[8..]);
        long positionMs = payload.Length == 24 ? BinaryPrimitives.ReadInt64LittleEndian(payload.Span[16..]) : 0;
        if (token <= 0 || epoch <= 0 || positionMs < 0) return;
        App.MainWindow.DispatcherQueue.TryEnqueue(() => AcceptGapless(token, epoch, positionMs));
    }

    private void GaplessTransitionProgressChanged()
        => App.MainWindow.DispatcherQueue.TryEnqueue(PresentLatestAudibleTransition);

    private void PresentLatestAudibleTransition()
    {
        if (_disposed || !state.CanStartPlayback || !ipc.TryGetProgressSnapshot(out var progress)) return;
        var playback = state.State.Playback;
        if (playback.TransitionPresentation is { Token: > 0, IsCommitted: false } shown && shown.Token != progress.TransitionToken)
        {
            if (progress.GaplessToken == shown.Token) AcceptGapless(shown.Token, progress.Epoch, progress.CurrentMs);
            else RollBackPlanPresentation(shown.Token);
        }
        ClearConfirmedPresentation(progress);

        if (progress.TransitionToken <= 0 || progress.TransitionEpoch <= 0
            || _nextPlan is not { } plan || plan.Token != progress.TransitionToken || plan.Epoch != progress.Epoch
            || plan.SelectionVersion != _selectionVersion || state.CurrentPlayingMusic != plan.From) return;
        if (playback.PendingSelection is { } pending && (!HasManualPreview || pending.EntryId != plan.EntryId)) return;
        if (playback.TransitionPresentation is { } current && current.Token == plan.Token && current.Epoch == progress.TransitionEpoch) return;
        playback.TransitionPresentation = new(plan.Next, plan.EntryId, plan.Token, progress.TransitionEpoch,
            progress.TransitionCurrentMs, progress.TransitionTotalMs);
    }

    private void RollBackPlanPresentation(long planToken)
    {
        var playback = state.State.Playback;
        if (playback.TransitionPresentation is not { IsCommitted: false } shown || shown.Token != planToken) return;
        // A manual selection still owns its preview when the fade falls back to an ordinary switch.
        playback.TransitionPresentation = HasManualPreview && playback.PendingSelection?.EntryId == shown.EntryId
            ? new PlaybackTransitionPresentation(shown.Music, shown.EntryId, DurationMs: shown.DurationMs)
            : null;
    }

    private void AcceptGapless(long token, long epoch, long positionMs)
    {
        if (_disposed) return;
        if (_nextPlan is { } plan && plan.Token == token) AcceptCommittedGapless(plan, epoch, positionMs);
        // While cancelling, only the ordered reply decides whether this token committed.
        // Transition notifications use a separate pipe and can arrive out of order.
        else if (_cancellingPlan?.Token == token) _cancellingTransition = (epoch, positionMs);
    }

    private void AcceptCommittedGapless(NextPlan plan, long epoch, long positionMs)
    {
        bool manual = IsManualNextPending && state.State.Playback.PendingSelection?.EntryId == plan.EntryId;
        if (_disposed || !state.CanStartPlayback || plan.Token <= _lastAcceptedToken
            || plan.SelectionVersion != _selectionVersion || state.CurrentPlayingMusic != plan.From
            || (!manual && state.State.Playback.PendingSelection != null)) return;
        _lastAcceptedToken = plan.Token;
        _activeLocalPlaybackId = plan.Token;
        _lastEndedEpoch = 0;
        _supersededHandoff = null;
        _deferredNaturalEnd = false;
        if (manual) _manualNextVersion = 0;
        _nextPlan = null;
        _planConfirmed = false;
        _planSending = false;
        var previousPresentation = _presentation;
        _presentation = new CancellationTokenSource();
        var cancellation = _presentation.Token;
        _acceptingGapless = true;
        try
        {
            bool lyricsAlreadyPresented = IsPresenting(plan.Next, plan.EntryId);
            var preview = state.State.Playback.TransitionPresentation;
            bool hasProgress = ipc.TryGetProgressSnapshot(out var confirmed) && confirmed.GaplessToken == plan.Token;
            if (!hasProgress)
            {
                // A command or critical notification can beat progress on the state pipe.
                // Keep the incoming clock through that gap and never use the outgoing position.
                if (preview is { } shown && shown.Token == plan.Token)
                {
                    if (epoch <= 0) epoch = shown.Epoch;
                    positionMs = Math.Max(positionMs, shown.PositionMs);
                }
                long duration = preview is { } projected && projected.Token == plan.Token && projected.DurationMs > 0
                    ? projected.DurationMs : (long)plan.Next.Duration.TotalMilliseconds;
                confirmed = new(0, epoch, positionMs, duration,
                    System.Diagnostics.Stopwatch.GetTimestamp(), state.IsPlaying, GaplessToken: plan.Token);
            }
            state.State.Queue.SelectEntry(plan.EntryId, plan.Next);
            BeginVersionIntent(plan.Next, plan.EntryId, state.State.Queue.RequestedVersion(plan.EntryId) > 0, true);
            _versionConfirmed = true;
            _activeIntent = (plan.Next, plan.EntryId, _intentExact, _intentMayAdvance, true);
            _activeSelectionVersion = _selectionVersion;
            _playingVersion = _selectionVersion;
            state.CurrentPlayingMusic = plan.Next;
            _previewNextVersion = 0;
            state.State.Playback.TransitionPresentation = hasProgress ? null
                : new PlaybackTransitionPresentation(plan.Next, plan.EntryId, plan.Token, confirmed.Epoch,
                    confirmed.CurrentMs, confirmed.TotalMs, IsCommitted: true);
            if (manual) SetPending(null);
            state.RemotePlaybackStatus = "";
            try { statistics.StartSession(plan.Next); _ = tasks.RunAsync(_ => statistics.RecordPlaybackStartAsync(plan.Next)); }
            catch (Exception ex) { logger.LogError(ex, "记录无缝播放统计失败"); }
            if (!lyricsAlreadyPresented)
            {
                state.UILyrics = [];
                state.LoadLyricsToUI(plan.Next);
            }
            if (hasProgress) state.UpdateProgressTimerUI();
            TrackStarted?.Invoke(plan.Next, confirmed.CurrentMs, cancellation);
            PlaybackConfirmed?.Invoke(plan.Next, confirmed);
        }
        finally
        {
            // A cancellation can resume the old selection on this dispatcher inline.
            // Publish the handoff before releasing its waiter.
            try { previousPresentation?.Cancel(); }
            finally
            {
                previousPresentation?.Dispose();
                _acceptingGapless = false;
            }
        }
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void StopGapless()
    {
        CancelGaplessPlan();
        state.State.Playback.TransitionPresentation = null;
        _cancellingPlan = null;
        _supersededHandoff = null;
        _gaplessCancellationSettled?.TrySetResult();
        _gaplessCancellationSettled = null;
        if (_gaplessTimer != null)
        {
            _gaplessTimer.Stop();
            _gaplessTimer.Tick -= GaplessTick;
        }
        if (_gaplessIpc != null)
        {
            _gaplessIpc.NotificationReceived -= GaplessNotification;
            _gaplessIpc.DspStateChanged -= GaplessOutputChanged;
            _gaplessIpc.TransitionProgressChanged -= GaplessTransitionProgressChanged;
        }
        state.PropertyChanged -= GaplessStateChanged;
        state.State.Queue.PropertyChanged -= GaplessQueueChanged;
        AppSettings.PlaybackTransitionsChanged -= GaplessPreferencesChanged;
        UnobserveCandidates();
        _candidateFrom = _candidateNext = null;
    }
}
