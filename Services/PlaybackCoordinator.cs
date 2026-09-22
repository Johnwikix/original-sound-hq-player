using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>所有选曲入口的播放用例；页面只接收已开始曲目的展示事件，不拥有播放任务。</summary>
public sealed class PlaybackCoordinator(AppViewModel state, BassPlayerCommandService player,
    PlaybackStatsService statistics, ApplicationTasks tasks, ShutdownCoordinator shutdown,
    ILogger<PlaybackCoordinator> logger, RemotePlaybackService remote) : IDisposable
{
    private CancellationTokenSource? _presentation;
    private bool _disposed;
    private bool _registered;
    public event Action<Music, CancellationToken>? TrackStarted;

    public Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= state.CurrentPlayingList.Count) return Task.CompletedTask;
        return PlayAsync(state.CurrentPlayingList[index], state.State.Queue.EntryIdAt(index));
    }

    public Task PlayAsync(Music music, long entryId = 0)
    {
        if (_disposed || !state.CanStartPlayback || music is null) return Task.CompletedTask;
        if (!_registered)
        {
            _registered = true;
            shutdown.RegisterCleanup(Dispose);
        }
        return tasks.RunAsync(_ => PlayCoreAsync(music, entryId));
    }

    private async Task PlayCoreAsync(Music music, long entryId)
    {
        if (_disposed || !state.CanStartPlayback) return;
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = new CancellationTokenSource();
        var token = _presentation.Token;
        try
        {
            long remoteGeneration = music.IsRemote ? remote.BeginSelection() : 0;
            if (!music.IsRemote)
            {
                await remote.StopAsync();
                token.ThrowIfCancellationRequested();
                player.PlayMusic(music);
            }
            await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
            {
                if (_disposed || !state.CanStartPlayback || token.IsCancellationRequested) return;
                state.State.Queue.SelectEntry(entryId, music);
                state.CurrentPlayingMusic = music;
                state.RemotePlaybackStatus = music.IsRemote ? Utils.ToolUtils.GetString("WebDavOpening") : "";
                if (music.IsRemote) { state.IsPlaying = false; state.StopProgressTimer(); }
                try { if (music.IsRemote) statistics.FlushSession(); else statistics.StartSession(music); }
                catch (Exception ex) { logger.LogError(ex, "记录播放统计会话失败"); }
                state.UILyrics = [];
                state.LoadLyricsToUI(music);
                state.UpdateProgressTimerUI();
                TrackStarted?.Invoke(music, token);
            });
            if (music.IsRemote && !token.IsCancellationRequested) await remote.PlayAsync(music, remoteGeneration, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogError(ex, "播放音乐失败"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _presentation?.Cancel();
        _presentation?.Dispose();
        _presentation = null;
        TrackStarted = null;
    }
}
