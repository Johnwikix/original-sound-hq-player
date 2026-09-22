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
    private long _generation, _intentVersion, _seekId;
    private bool _intent = true, _active, _needsRestart;
    private Snapshot? _snapshot;
    private sealed record Snapshot(ProgressSnapshot Progress);
    public event Action? Ended;
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
            lock (_gate) { if (generation != _generation) return; }
            var (source, track) = await library.ResolveAsync(music);
            token.ThrowIfCancellationRequested();
            lock (_gate) { if (generation != _generation) return; }
            _sessionCancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var connection = library.Connect(source);
            var session = await Task.Run(() => new RemoteReadSession(transport, connection, WebDavLibraryService.ToEntry(track), cache, music.Path));
            _bridge = new WebDavPlaybackBridge(session);
            _bridge.SetPlaying(false);
            _bridge.Start();
            _sessionId = Guid.NewGuid();
            var reply = await _client.PrepareAsync(new PlaybackSource
            {
                Kind = PlaybackSourceKind.Http, ResourceId = music.Path, Location = _bridge.Location,
                FileExtension = System.IO.Path.GetExtension(track.Href),
                CanSeek = track.Length > 0
            }, _sessionId, token);
            if (!reply.Accepted) throw new WebDavException("PlaybackFailed");
            token.ThrowIfCancellationRequested();
            bool current;
            lock (_gate) current = generation == _generation;
            if (!current) { await StopSessionCoreAsync(); return; }
            await SetIntentAsync(null);
            _monitor = MonitorAsync(music, _sessionId, generation, _sessionCancel.Token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { await StopSessionCoreAsync(); }
        catch (Exception ex)
        {
            await StopSessionCoreAsync();
            ReportFailure(ex is WebDavException dav ? dav.Code : "PlaybackFailed", generation);
        }
        finally { _switch.Release(); }
    }
    public async Task SetIntentAsync(bool? playing)
    {
        lock (_gate)
        {
            if (!_active) return;
            if (playing.HasValue) { _intent = playing.Value; _intentVersion++; }
        }
        await _control.WaitAsync();
        try
        {
            while (true)
            {
                Guid id = _sessionId;
                if (id == Guid.Empty || _sessionCancel?.IsCancellationRequested != false) return;
                bool intent; long version;
                lock (_gate) { intent = _intent; version = _intentVersion; }
                var response = intent ? await _client.PlayAsync(id, _sessionCancel.Token) : await _client.PauseAsync(id, _sessionCancel.Token);
                if (!response.Accepted) return;
                _bridge?.SetPlaying(intent && response.Phase == StreamPhase.Playing);
                lock (_gate) { if (version == _intentVersion) return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { ReportFailure("PlaybackFailed", _generation); }
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
                var reply = await _client.StatusAsync(id, token);
                if (!reply.Accepted || reply.SessionId != id) throw new WebDavException("PlaybackFailed");
                lock (_gate)
                {
                    if (generation != _generation) return;
                    _snapshot = new(new ProgressSnapshot(0, -generation, reply.PositionMs, reply.DurationMs ?? 0,
                        Stopwatch.GetTimestamp(), reply.Phase == StreamPhase.Playing, reply.SeekId));
                }
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
                if (reply.Phase is StreamPhase.Failed) { ReportFailure(_bridge?.Error ?? "PlaybackFailed", generation); return; }
                if (reply.Phase is StreamPhase.Ended or StreamPhase.Stopped) return;
            } while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { ReportFailure("PlaybackFailed", generation); }
    }
    private void ReportFailure(string code, long generation)
    {
        lock (_gate)
        {
            if (generation != _generation) return;
            _intent = false;
            _needsRestart = true;
        }
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
        });
    }
    public async Task StopAsync()
    {
        long generation;
        lock (_gate) { generation = ++_generation; _intent = false; }
        _sessionCancel?.Cancel();
        await _switch.WaitAsync();
        try
        {
            await StopSessionCoreAsync();
            lock (_gate) { if (generation == _generation) { _active = false; _snapshot = null; } }
        }
        finally { _switch.Release(); }
    }
    private async Task StopSessionCoreAsync()
    {
        _sessionCancel?.Cancel();
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
                _sessionCancel?.Dispose();
                _sessionCancel = null;
            }
        }
        finally { _control.Release(); }
    }
}
