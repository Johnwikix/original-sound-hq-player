using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>所有选曲与失败恢复入口；立即发布待播选择，开始播放时再提交曲目展示与统计。</summary>
public sealed class PlaybackCoordinator(AppViewModel state, BassPlayerCommandService player,
    PlaybackStatsService statistics, ApplicationTasks tasks, ShutdownCoordinator shutdown,
    ILogger<PlaybackCoordinator> logger, RemotePlaybackService remote, WebDavLibraryService library) : IDisposable
{
    private CancellationTokenSource? _presentation;
    private bool _disposed;
    private bool _registered;
    private long _selectionVersion;
    private long _playingVersion, _playingGeneration;
    private readonly HashSet<long> _failedEntries = [];
    private (Music Music, long Generation, bool Advance)? _deferredFailure;
    public event Action<Music, CancellationToken>? TrackStarted;
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

    public Task PlayAsync(Music music, long entryId = 0)
    {
        if (_disposed || !state.CanStartPlayback || music is null || (!music.IsRemote && !music.IsPlayable)) return Task.CompletedTask;
        _failedEntries.Clear();
        return RunSelectionAsync(async token =>
        {
            if (await IsAvailableAsync(music, token, retryImmediately: true)) await PlayCoreAsync(music, entryId, token);
        }, music, entryId != 0 ? entryId : state.State.Queue.EntryIdAt(state.State.Queue.IndexOf(music)));
    }

    private Task RunSelectionAsync(Func<CancellationToken, Task> operation, Music music, long entryId)
    {
        if (_disposed || !state.CanStartPlayback) return Task.CompletedTask;
        long version = ++_selectionVersion;
        _presentation?.Cancel(); // 收到新意图即停止上一轮等待，不等 ApplicationTasks 的下一次调度。
        SetPending(new(music, entryId));
        if (!_registered)
        {
            _registered = true;
            shutdown.RegisterCleanup(Dispose);
            remote.Failed += OnRemoteFailed;
        }
        return tasks.RunAsync(stoppingToken => RunSelectionCoreAsync(operation, stoppingToken, version));
    }

    private async Task RunSelectionCoreAsync(Func<CancellationToken, Task> operation, CancellationToken stoppingToken, long version)
    {
        if (_disposed || !state.CanStartPlayback || version != _selectionVersion) return;
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
                SetPending(null);
                _playingVersion = version;
                var failure = _deferredFailure;
                _deferredFailure = null;
                if (failure is { } deferred) OnRemoteFailed(deferred.Music, deferred.Generation, deferred.Advance);
            }
        }
    }

    public void CancelPendingSelection()
    {
        _playingVersion = ++_selectionVersion;
        _deferredFailure = null;
        _presentation?.Cancel();
        SetPending(null);
    }

    private void SetPending(PlaybackSelection? selection)
    {
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
        _failedEntries.Add(state.State.Queue.CurrentEntryId);
        int count = state.CurrentPlayingList.Count;
        if (count == 0) { player.MusicEnd(); return; }
        int index = (state.GetCurrentIndex() + 1) % count;
        _ = RunSelectionAsync(token => PlayQueueCoreAsync(index, 1, true, token),
            state.CurrentPlayingList[index], state.State.Queue.EntryIdAt(index));
    }

    private async Task PlayQueueCoreAsync(int index, int direction, bool stopWhenUnavailable, CancellationToken token)
    {
        var list = state.CurrentPlayingList;
        int count = list.Count;
        long originalEntry = state.State.Queue.EntryIdAt(state.GetCurrentIndex());
        // 最多一遍队列；跨请求的来源重试期限由 library 负责，不能在此跳过同源的缓存歌曲。
        for (int attempted = 0; attempted < count; attempted++)
        {
            token.ThrowIfCancellationRequested();
            if (_disposed || !state.CanStartPlayback || !ReferenceEquals(list, state.CurrentPlayingList) || list.Count != count) return;
            if (index < 0 || index >= count) return;
            var music = list[index];
            long entryId = state.State.Queue.EntryIdAt(index);
            if (attempted > 0 && entryId == originalEntry) break;
            SetPending(new(music, entryId));
            if (!_failedEntries.Contains(entryId))
            {
                bool available = await IsAvailableAsync(music, token);
                token.ThrowIfCancellationRequested();
                // 等待网络期间换队列、删除或重排条目时，不让旧导航请求覆盖新选择。
                if (!ReferenceEquals(list, state.CurrentPlayingList) || list.Count != count ||
                    state.State.Queue.EntryIdAt(index) != entryId || !ReferenceEquals(list[index], music)) return;
                if (available)
                {
                    await PlayCoreAsync(music, entryId, token);
                    return;
                }
            }
            index = (index + direction + count) % count;
        }
        if (stopWhenUnavailable && !_disposed && state.CanStartPlayback && !token.IsCancellationRequested) player.MusicEnd();
    }

    private async Task<bool> IsAvailableAsync(Music music, CancellationToken token, bool retryImmediately = false)
    {
        if (!music.IsRemote) return music.IsPlayable;
        try
        {
            return await library.CanPlayAsync(music, retryImmediately, token);
        }
        catch (WebDavException ex) when (ex.Code is "ResourceMissing" or "SourceUnavailable") { return false; }
    }

    private async Task PlayCoreAsync(Music music, long entryId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_disposed || !state.CanStartPlayback) return;
        long remoteGeneration = music.IsRemote ? remote.BeginSelection() : 0;
        _playingGeneration = remoteGeneration;
        _playingVersion = _selectionVersion;
        _deferredFailure = null;
        if (!music.IsRemote)
        {
            // 服务同步登记停止代次，后台执行可能同步 Flush(true) 的缓存收尾。
            await remote.StopAsync().WaitAsync(token);
            token.ThrowIfCancellationRequested();
            player.PlayMusic(music);
        }
        await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
        {
            if (_disposed || !state.CanStartPlayback || token.IsCancellationRequested) return;
            state.State.Queue.SelectEntry(entryId, music);
            state.CurrentPlayingMusic = music;
            state.State.Playback.HasRemoteBuffer = false;
            state.State.Playback.BufferedProgress = 0;
            state.RemotePlaybackStatus = music.IsRemote ? Utils.ToolUtils.GetString("WebDavOpening") : "";
            if (music.IsRemote) { state.IsPlaying = false; state.StopProgressTimer(); }
            try { if (music.IsRemote) statistics.FlushSession(); else statistics.StartSession(music); }
            catch (Exception ex) { logger.LogError(ex, "记录播放统计会话失败"); }
            state.UILyrics = [];
            state.LoadLyricsToUI(music);
            state.UpdateProgressTimerUI();
            TrackStarted?.Invoke(music, token);
        });
        // 远程准备包含 PasswordVault 和缓存文件前置工作；只有展示发布回 UI 线程。
        if (music.IsRemote && !token.IsCancellationRequested)
            await Task.Run(() => remote.PlayAsync(music, remoteGeneration, token), token);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = null;
        remote.Failed -= OnRemoteFailed;
        state.State.Playback.PendingSelection = null;
        SelectionChanged = null;
        TrackStarted = null;
    }
}
