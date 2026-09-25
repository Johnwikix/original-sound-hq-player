using BassPlayerIpc.Shared;
using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>远程播放控制与供数生命周期。会话身份隔离迟到状态，显式意图独立于实际缓冲状态。</summary>
public sealed class RemotePlaybackService(WebDavLibraryService library, WebDavTransport transport, RemoteAudioCache cache,
    AppViewModel state, PlaybackStatsService statistics, ILogger<RemotePlaybackService> logger)
{
    private readonly StreamingClient _client = new();
    private readonly SemaphoreSlim _switch = new(1, 1);
    private readonly SemaphoreSlim _control = new(1, 1);
    private readonly object _gate = new();
    private CancellationTokenSource? _sessionCancel;
    private WebDavPlaybackBridge? _bridge;
    private Task _monitor = Task.CompletedTask;
    private Guid _sessionId;
    private long _generation, _sessionGeneration, _intentVersion, _seekId;
    private bool _intent = true, _active, _needsRestart;
    private Snapshot? _snapshot;
    private Music? _music;
    private sealed record Snapshot(ProgressSnapshot Progress);
    public event Action? Ended;
    public event Action<Music, long, bool>? Failed;
    public bool IsActive { get { lock (_gate) return _active; } }
    public bool WantsPlay { get { lock (_gate) return _intent; } }
    public bool NeedsStart { get { lock (_gate) return !_active || _needsRestart; } }
    public ProgressSnapshot? GetProgress()
    {
        lock (_gate) return _active ? _snapshot?.Progress ?? new ProgressSnapshot(0, -_generation, 0, 0, Stopwatch.GetTimestamp(), false) : null;
    }

    public long BeginSelection()
    {
        lock (_gate) { _active = true; _needsRestart = false; _intent = true; _snapshot = null; _intentVersion++; return ++_generation; }
    }
    public async Task PlayAsync(Music music, long generation, CancellationToken token)
    {
        await _switch.WaitAsync(token);
        try
        {
            await StopSessionCoreAsync();
            token.ThrowIfCancellationRequested();
            lock (_gate) { if (generation != _generation) return; _music = music; }
            var (source, track) = await library.ResolveAsync(music);
            token.ThrowIfCancellationRequested();
            lock (_gate) { if (generation != _generation) return; }
            // 选曲等待和正在播放的会话有不同寿命：下次探活取消，不能停掉旧歌的监控。
            // 准备阶段由 token 中断并收尾；成功后只由会话 Stop/应用退出取消。
            lock (_gate) _sessionCancel = new CancellationTokenSource();
            // 完整缓存从租约读取，不要求网络或 PasswordVault；连接仅在缺失字节时建立。
            var session = new RemoteReadSession(transport, () => library.Connect(source), WebDavLibraryService.ToEntry(track), cache, music.Path);
            _bridge = new WebDavPlaybackBridge(session);
            if (!session.HasCompleteCache && library.IsSourceOffline(source.Id) &&
                !await library.EnsureAvailableAsync(source, token)) throw new WebDavException("SourceUnavailable");
            _bridge.SetPlaying(false);
            _bridge.Start();
            _sessionGeneration = generation;
            _sessionId = Guid.NewGuid();
            var reply = await _client.PrepareAsync(new PlaybackSource
            {
                Kind = PlaybackSourceKind.Http, ResourceId = music.Path, Location = _bridge.Location,
                FileExtension = System.IO.Path.GetExtension(track.Href),
                ContentLength = track.Length, ETag = track.ETag,
                CanSeek = track.Length > 0
            }, _sessionId, token);
            if (!reply.Accepted) throw new WebDavException("PlaybackFailed");
            token.ThrowIfCancellationRequested();
            bool current;
            lock (_gate) current = generation == _generation;
            if (!current) { await StopSessionCoreAsync(); return; }
            await SetIntentAsync(null).WaitAsync(token);
            token.ThrowIfCancellationRequested();
            _monitor = MonitorAsync(music, _sessionId, generation, _sessionCancel.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await StopSessionCoreAsync(); }
        catch (Exception ex)
        {
            var failure = _bridge?.SourceFailure;
            await StopSessionCoreAsync();
            ReportFailure(failure?.Code ?? (ex is WebDavException dav ? dav.Code : "PlaybackFailed"), generation, music, failure?.SourceUnavailable == true);
        }
        finally { _switch.Release(); }
    }
    public async Task SetIntentAsync(bool? playing)
    {
        long generation;
        Music? music;
        lock (_gate)
        {
            if (!_active) return;
            if (playing.HasValue) { _intent = playing.Value; _intentVersion++; }
            if (_needsRestart) return;
            generation = _generation;
            music = _music;
        }
        await _control.WaitAsync();
        try
        {
            while (true)
            {
                Guid id = _sessionId;
                if (id == Guid.Empty || _sessionCancel?.IsCancellationRequested != false) return;
                bool intent; long version;
                lock (_gate)
                {
                    if (generation != _generation || generation != _sessionGeneration) return;
                    intent = _intent;
                    version = _intentVersion;
                }
                var response = intent ? await _client.PlayAsync(id, _sessionCancel.Token) : await _client.PauseAsync(id, _sessionCancel.Token);
                if (!response.Accepted) return;
                _bridge?.SetPlaying(intent && response.Phase == StreamPhase.Playing);
                lock (_gate) { if (version == _intentVersion) return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (music is not null) ReportFailure("PlaybackFailed", generation, music); }
        finally { _control.Release(); }
    }
    public async Task SeekAsync(long milliseconds)
    {
        Guid id = _sessionId;
        if (id == Guid.Empty || _sessionCancel?.IsCancellationRequested != false) return;
        long seekId = Interlocked.Increment(ref _seekId);
        state.BeginProgressSeek(milliseconds, seekId);
        try
        {
            var response = await _client.SeekAsync(id, milliseconds, seekId, _sessionCancel.Token);
            if (!response.Accepted) state.CancelProgressSeek(seekId);
        }
        catch { state.CancelProgressSeek(seekId); }
    }
    private async Task MonitorAsync(Music music, Guid id, long generation, CancellationToken token)
    {
        bool started = false;
        StreamPhase? previous = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
            do
            {
                // 源端读取失败可能先于解码器耗尽缓冲；不等播放器超时才恢复队列。
                if (_bridge?.SourceFailure is { } failure)
                {
                    ReportFailure(failure.Code, generation, music, failure.SourceUnavailable);
                    return;
                }
                var reply = await _client.StatusAsync(id, token);
                if (_bridge?.SourceFailure is { } failureAfterStatus)
                {
                    ReportFailure(failureAfterStatus.Code, generation, music, failureAfterStatus.SourceUnavailable);
                    return;
                }
                if (!reply.Accepted || reply.SessionId != id) throw new WebDavException("PlaybackFailed");
                lock (_gate)
                {
                    if (generation != _generation) return;
                    _snapshot = new(new ProgressSnapshot(0, -generation, reply.PositionMs, reply.DurationMs ?? 0,
                        Stopwatch.GetTimestamp(), reply.Phase == StreamPhase.Playing, reply.SeekId));
                }
                // Duration may arrive while the stream stays in Buffering; its timer is stopped then.
                if (reply.DurationMs is > 0)
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (token.IsCancellationRequested || generation != _generation || !state.CanPublishState) return;
                        if (state.ProgressSliderMax != reply.DurationMs.Value / 1000.0)
                            state.UpdateProgressTimerUI();
                    });
                _bridge?.SetPlaying(reply.Phase == StreamPhase.Playing);
                if (reply.Phase != previous)
                {
                    previous = reply.Phase;
                    await App.MainWindow.DispatcherQueue.EnqueueAsync(() =>
                    {
                        if (token.IsCancellationRequested || generation != _generation || !state.CanPublishState) return;
                        state.IsPlaying = reply.Phase == StreamPhase.Playing;
                        state.RemotePlaybackStatus = ToolUtils.GetString("WebDav" + reply.Phase);
                        if (reply.DurationMs is > 0) music.Duration = TimeSpan.FromMilliseconds(reply.DurationMs.Value);
                        if (state.IsPlaying)
                        {
                            if (!started) { statistics.StartSession(music); started = true; }
                            state.StartProgressTimer();
                        }
                        else state.StopProgressTimer();
                        state.UpdateProgressTimerUI();
                        if (reply.Phase == StreamPhase.Ended)
                        {
                            lock (_gate) { _intent = false; _needsRestart = true; }
                            Ended?.Invoke();
                        }
                    });
                }
                if (reply.Phase is StreamPhase.Failed)
                {
                    var readFailure = _bridge?.SourceFailure;
                    ReportFailure(readFailure?.Code ?? _bridge?.Error ?? "PlaybackFailed", generation, music, readFailure?.SourceUnavailable == true);
                    return;
                }
                if (reply.Phase is StreamPhase.Ended or StreamPhase.Stopped) return;
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { ReportFailure("PlaybackFailed", generation, music); }
    }
    private void ReportFailure(string code, long generation, Music music, bool sourceUnavailable = false)
    {
        bool advance;
        long intentVersion;
        lock (_gate)
        {
            if (generation != _generation || _needsRestart) return;
            advance = _intent;
            intentVersion = _intentVersion;
            _intent = false;
            _needsRestart = true;
        }
        if (sourceUnavailable) library.ReportSourceFailure(music.SourceId);
        logger.LogWarning("Remote playback failed: {Code}", code);
        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
        {
            if (generation != _generation || !state.CanPublishState) return;
            state.IsPlaying = false;
            state.StopProgressTimer();
            state.RemotePlaybackStatus = ToolUtils.GetString("WebDavFailed");
            state.State.Shell.InfoBarTitle = ToolUtils.GetString("Error");
            state.State.Shell.InfoBarMessage = WebDavText.Error(code);
            state.State.Shell.InfoBarIsOpen = true;
            // 错误入队后用户仍可能暂停；恢复不能覆盖更晚的显式意图。
            bool shouldAdvance;
            lock (_gate) shouldAdvance = advance && intentVersion == _intentVersion;
            Failed?.Invoke(music, generation, shouldAdvance);
        });
    }
    public Task StopAsync()
    {
        long generation;
        lock (_gate)
        {
            generation = ++_generation;
            _intent = false;
            _sessionCancel?.Cancel();
        }
        // 先同步记录停止意图，磁盘 Flush 和会话清理留在后台；迟到的停止不能覆盖新选曲。
        return Task.Run(() => StopCoreAsync(generation));
    }
    private async Task StopCoreAsync(long generation)
    {
        await _switch.WaitAsync();
        try
        {
            lock (_gate) { if (generation != _generation) return; }
            await StopSessionCoreAsync();
            lock (_gate) { if (generation == _generation) { _active = false; _snapshot = null; } }
        }
        finally { _switch.Release(); }
    }
    private async Task StopSessionCoreAsync()
    {
        lock (_gate) _sessionCancel?.Cancel();
        await _monitor;
        _monitor = Task.CompletedTask;
        await _control.WaitAsync();
        try
        {
            if (_sessionId != Guid.Empty)
            {
                try { await _client.StopAsync(_sessionId); }
                catch (Exception) { logger.LogWarning("Remote playback stop was not acknowledged"); }
            }
            _sessionId = Guid.Empty;
            try { if (_bridge is not null) await _bridge.DisposeAsync(); }
            catch (Exception) { logger.LogWarning("Remote playback cleanup failed"); }
            finally
            {
                _bridge = null;
                lock (_gate)
                {
                    _sessionCancel?.Dispose();
                    _sessionCancel = null;
                }
            }
        }
        finally { _control.Release(); }
    }
}
