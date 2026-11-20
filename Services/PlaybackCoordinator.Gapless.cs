using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Buffers.Binary;
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
        long EntryId, long Epoch, string Path, TrackTransitionStyle Style, int CrossfadeSeconds, bool PreserveAlbumBoundary);
    private NextPlan? _nextPlan;
    private NextPlan? _cancellingPlan;
    private (long Epoch, long PositionMs) _cancellingTransition;
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

    internal void StartGapless()
    {
        if (_disposed) return;
        if (_gaplessIpc == null)
        {
            RegisterPlaybackLifecycle();
            _gaplessIpc = ipc;
            _gaplessIpc.NotificationReceived += GaplessNotification;
            _gaplessIpc.DspStateChanged += GaplessOutputChanged;
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
        if (!_disposed && state.CanStartPlayback && (_cancellingPlan != null
            || AppSettings.Dsp.GaplessPlayback && state.IsPlaying && state.CurrentPlayingMusic is { IsRemote: false }
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
        RefreshGaplessPlan();
    }

    private void CandidateMusicChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(Music.Path) or nameof(Music.IsPlayable) or nameof(Music.Album)
            or nameof(Music.AlbumArtist) or nameof(Music.Author) or nameof(Music.DiskNumber) or nameof(Music.TrackNumber))) return;
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
            || current.SongId > 0 && current.SongId == next.SongId
            || !string.IsNullOrWhiteSpace(current.Path) && string.Equals(current.Path, next.Path, StringComparison.OrdinalIgnoreCase))
            return true;

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
        // Keep the old identity until its ordered cancellation reply: the render thread
        // may already have committed it even when its transition notification is late.
        if (_cancellingPlan is { } cancelling)
        {
            if (!_cancelSending && Environment.TickCount64 >= _cancelRetryAfter)
                _ = CancelGaplessPlanAsync(cancelling);
            return;
        }
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
        if (plan is not null && ((hasProgress && plan.Epoch != progress.Epoch) || plan.EntryId != _candidateEntry
            || plan.From != current || plan.Next != next || plan.SelectionVersion != _selectionVersion || plan.Path != next.Path
            || plan.Style != settings.TransitionStyle || plan.CrossfadeSeconds != settings.CrossfadeSeconds
            || plan.PreserveAlbumBoundary != preserveAlbumBoundary))
        {
            CancelGaplessPlan();
            return;
        }
        if (!hasProgress || !progress.Playing) return;
        if (plan is null)
        {
            plan = new(++_nextToken, _selectionVersion, current, next, _candidateEntry, progress.Epoch, next.Path,
                settings.TransitionStyle, settings.CrossfadeSeconds, preserveAlbumBoundary);
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
        try { accepted = await _gaplessIpc!.QueueNextAsync(new(plan.Epoch, plan.Token, plan.Path, plan.PreserveAlbumBoundary)); }
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
        _cancellingTransition = default;
        UpdateGaplessTimer();
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
        var transition = _cancellingTransition;
        _cancellingPlan = null;
        _cancellingTransition = default;
        UpdateGaplessTimer();
        if (committed == plan.Token) AcceptCommittedGapless(plan, transition.Epoch, transition.PositionMs);
        RefreshGaplessPlan();
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
            BeginVersionIntent(plan.Next, plan.EntryId, state.State.Queue.RequestedVersion(plan.EntryId) > 0, true);
            _versionConfirmed = true;
            _activeIntent = (plan.Next, plan.EntryId, _intentExact, _intentMayAdvance, true);
            _activeSelectionVersion = _selectionVersion;
            state.CurrentPlayingMusic = plan.Next;
            state.RemotePlaybackStatus = "";
            try { statistics.StartSession(plan.Next); _ = tasks.RunAsync(_ => statistics.RecordPlaybackStartAsync(plan.Next)); }
            catch (Exception ex) { logger.LogError(ex, "记录无缝播放统计失败"); }
            state.UILyrics = [];
            state.LoadLyricsToUI(plan.Next);
            // The transition notification can beat the state-pipe snapshot. Its committed token,
            // not the previous track's cached position, identifies this occurrence.
            bool hasProgress = ipc.TryGetProgressSnapshot(out var confirmed) && confirmed.GaplessToken == plan.Token;
            if (!hasProgress)
                confirmed = new(0, epoch, positionMs, (long)plan.Next.Duration.TotalMilliseconds,
                    System.Diagnostics.Stopwatch.GetTimestamp(), true, GaplessToken: plan.Token);
            else state.UpdateProgressTimerUI();
            TrackStarted?.Invoke(plan.Next, cancellation);
            PlaybackConfirmed?.Invoke(plan.Next, confirmed);
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
        AppSettings.PlaybackTransitionsChanged -= GaplessPreferencesChanged;
        UnobserveCandidates();
        _candidateFrom = _candidateNext = null;
    }
}
