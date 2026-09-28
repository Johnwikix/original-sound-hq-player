using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using static WinUIMusicPlayer.Utils.ToolUtils;

namespace WinUIMusicPlayer.Services;

public sealed partial class PlaybackCoordinator
{
    // SelectionVersion rejects an old user intent; Epoch rejects an old engine timeline;
    // Token identifies this exact plan, including duplicate queue entries.
    private sealed record NextPlan(long Token, long SelectionVersion, Music From, Music Next,
        long EntryId, long Epoch, string Path);
    private NextPlan? _nextPlan;
    private NextPlan? _cancellingPlan;
    private IpcService? _gaplessIpc;
    private DispatcherQueueTimer? _gaplessTimer;
    private long _nextToken, _lastAcceptedToken, _retryAfter, _outputGeneration;
    private bool _planConfirmed, _planSending, _acceptingGapless;
    private bool _cancelSending;
    private long _cancelRetryAfter;
    private bool _candidateDirty = true;
    private long _candidateQueueVersion = -1, _candidateCurrentEntry, _candidateEntry;
    private PlayMode _candidateMode;
    private Music? _candidateFrom, _candidateNext;

    private void StartGapless()
    {
        if (_disposed) return;
        if (_gaplessIpc == null)
        {
            _gaplessIpc = ipc;
            _gaplessIpc.NotificationReceived += GaplessNotification;
            _gaplessIpc.DspStateChanged += GaplessOutputChanged;
            _outputGeneration = _gaplessIpc.CurrentDspState?.State.OutputGeneration ?? 0;
            state.PropertyChanged += GaplessStateChanged;
            state.State.Queue.PropertyChanged += GaplessQueueChanged;
            AppSettings.AudioResponseChanged += GaplessPreferencesChanged;
            _gaplessTimer = App.MainWindow.DispatcherQueue.CreateTimer();
            _gaplessTimer.Interval = TimeSpan.FromMilliseconds(500);
            _gaplessTimer.Tick += GaplessTick;
        }
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void UpdateGaplessTimer()
    {
        if (!_disposed && AppSettings.Dsp.GaplessPlayback && state.IsPlaying
            && state.CurrentPlayingMusic is { IsRemote: false } && state.CurrentPlayMode != PlayMode.RepeatOff)
            _gaplessTimer?.Start();
        else _gaplessTimer?.Stop();
    }

    private void GaplessPreferencesChanged(object? sender, EventArgs e)
        => App.MainWindow.DispatcherQueue.TryEnqueue(RefreshGaplessPreferences);

    private void RefreshGaplessPreferences()
    {
        if (_disposed) return;
        UpdateGaplessTimer();
        RefreshGaplessPlan();
    }

    private void GaplessOutputChanged()
        => App.MainWindow.DispatcherQueue.TryEnqueue(RefreshGaplessOutput);

    private void RefreshGaplessOutput()
    {
        if (_disposed) return;
        long generation = _gaplessIpc?.CurrentDspState?.State.OutputGeneration ?? 0;
        if (generation == _outputGeneration) return;
        _outputGeneration = generation;
        // An output rebuild can invalidate preparation without changing the timeline epoch.
        // The old confirmation cannot satisfy this new output, even if its reply arrives late.
        CancelGaplessPlan();
        RefreshGaplessPlan();
    }

    private void GaplessStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_acceptingGapless) return;
        if (e.PropertyName is nameof(state.IsPlaying) or nameof(state.CurrentPlayingMusic))
        {
            UpdateGaplessTimer();
            CancelGaplessPlan();
            RefreshGaplessPlan();
        }
        // Queue mode/list notifications precede rebuilding the order. Use its committed Version.
    }

    private void GaplessQueueChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(state.State.Queue.Version)) return;
        _candidateDirty = true;
        UpdateGaplessTimer();
        CancelGaplessPlan();
        RefreshGaplessPlan();
    }

    private void CandidateMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Music.Path) or nameof(Music.IsPlayable))) return;
        _candidateDirty = true;
        CancelGaplessPlan();
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
        if (_candidateMode == PlayMode.SingleLoop)
        {
            _candidateNext = current;
            _candidateEntry = queue.CurrentEntryId;
        }
        else
        {
            int index = PlaybackCommands.FindCandidateIndex(state.CurrentPlayingList, state.GetCurrentIndex(), 1,
                allowSingleEntryReplay: true);
            if (index >= 0)
            {
                _candidateNext = state.CurrentPlayingList[index];
                _candidateEntry = queue.EntryIdAt(index);
            }
        }
        current.PropertyChanged += CandidateMusicChanged;
        if (_candidateNext != null && _candidateNext != current)
            _candidateNext.PropertyChanged += CandidateMusicChanged;
    }

    private void GaplessTick(DispatcherQueueTimer sender, object args) => RefreshGaplessPlan();

    private void RefreshGaplessPlan()
    {
        if (_disposed || !state.CanStartPlayback || _gaplessIpc == null || _acceptingGapless) return;
        // Keep the old identity until its ordered cancellation reply: the render thread
        // may already have committed it even when its transition notification is late.
        if (_cancellingPlan is { } cancelling)
        {
            if (!_cancelSending && Environment.TickCount64 >= _cancelRetryAfter)
                _ = CancelGaplessPlanAsync(cancelling);
            return;
        }
        if (!state.IsPlaying || !AppSettings.Dsp.GaplessPlayback || state.State.Playback.PendingSelection != null
            || state.CurrentPlayingMusic is not { IsRemote: false, IsPlayable: true } current
            || state.CurrentPlayMode == PlayMode.RepeatOff)
        {
            CancelGaplessPlan();
            return;
        }
        if (!_gaplessIpc.TryGetProgressSnapshot(out var progress) || progress.Epoch <= 0) return;
        if (_nextPlan is { } completed && progress.GaplessToken == completed.Token)
        {
            AcceptGapless(completed.Token);
            return;
        }
        ResolveCandidate(current);
        if (_candidateNext is not { IsRemote: false, IsPlayable: true } next) { CancelGaplessPlan(); return; }
        if (_nextPlan is not { } plan || plan.Epoch != progress.Epoch || plan.EntryId != _candidateEntry
            || plan.From != current || plan.Next != next || plan.SelectionVersion != _selectionVersion || plan.Path != next.Path)
        {
            _nextPlan = new(++_nextToken, _selectionVersion, current, next, _candidateEntry, progress.Epoch, next.Path);
            _planConfirmed = false;
            _planSending = false;
            _retryAfter = 0;
        }
        if (_planConfirmed || _planSending || Environment.TickCount64 < _retryAfter) return;
        _planSending = true;
        _ = SendGaplessPlanAsync(_nextPlan);
    }

    private async Task SendGaplessPlanAsync(NextPlan plan)
    {
        bool accepted = false;
        try { accepted = await _gaplessIpc!.QueueNextAsync(new(plan.Epoch, plan.Token, plan.Path)); }
        catch (Exception ex) { logger.LogWarning(ex, "无法提交下一曲预载计划，保留普通切歌"); }
        if (_disposed || !ReferenceEquals(_nextPlan, plan)) return;
        _planSending = false;
        _planConfirmed = accepted;
        _retryAfter = Environment.TickCount64 + 1000;
    }

    private void CancelGaplessPlan()
    {
        if (_nextPlan is not { } plan) return;
        _nextPlan = null;
        _planConfirmed = false;
        _planSending = false;
        _cancellingPlan = plan;
        _ = CancelGaplessPlanAsync(plan);
    }

    private async Task CancelGaplessPlanAsync(NextPlan plan)
    {
        _cancelSending = true;
        bool confirmed = false;
        long committed = 0;
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
        _cancellingPlan = null;
        if (committed == plan.Token) AcceptCommittedGapless(plan);
        RefreshGaplessPlan();
    }

    private void GaplessNotification(MessageTypeId type, ReadOnlyMemory<byte> payload)
    {
        if (type != MessageTypeId.GaplessTransition || payload.Length != 16) return;
        long token = System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(payload.Span);
        App.MainWindow.DispatcherQueue.TryEnqueue(() => AcceptGapless(token));
    }

    private void AcceptGapless(long token)
    {
        if (_nextPlan is { } plan && plan.Token == token) AcceptCommittedGapless(plan);
        // While cancelling, only the ordered reply decides whether this token committed.
        // Transition notifications use a separate pipe and can arrive out of order.
    }

    private void AcceptCommittedGapless(NextPlan plan)
    {
        if (_disposed || !state.CanStartPlayback || plan.Token <= _lastAcceptedToken
            || plan.SelectionVersion != _selectionVersion || state.CurrentPlayingMusic != plan.From
            || state.State.Playback.PendingSelection != null) return;
        _lastAcceptedToken = plan.Token;
        _nextPlan = null;
        _planConfirmed = false;
        _planSending = false;
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = new CancellationTokenSource();
        var cancellation = _presentation.Token;
        _acceptingGapless = true;
        try
        {
            state.State.Queue.SelectEntry(plan.EntryId, plan.Next);
            state.CurrentPlayingMusic = plan.Next;
            state.RemotePlaybackStatus = "";
            try { statistics.StartSession(plan.Next); }
            catch (Exception ex) { logger.LogError(ex, "记录无缝播放统计失败"); }
            state.UILyrics = [];
            state.LoadLyricsToUI(plan.Next);
            state.UpdateProgressTimerUI();
            TrackStarted?.Invoke(plan.Next, cancellation);
        }
        finally { _acceptingGapless = false; }
        RefreshGaplessPlan();
    }

    private void StopGapless()
    {
        CancelGaplessPlan();
        if (_gaplessTimer != null)
        {
            _gaplessTimer.Stop();
            _gaplessTimer.Tick -= GaplessTick;
        }
        if (_gaplessIpc != null)
        {
            _gaplessIpc.NotificationReceived -= GaplessNotification;
            _gaplessIpc.DspStateChanged -= GaplessOutputChanged;
        }
        state.PropertyChanged -= GaplessStateChanged;
        state.State.Queue.PropertyChanged -= GaplessQueueChanged;
        AppSettings.AudioResponseChanged -= GaplessPreferencesChanged;
        UnobserveCandidates();
        _candidateFrom = _candidateNext = null;
    }
}
