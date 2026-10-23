using BassPlayerIpc.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services.LastFm;

/// <summary>Observes existing playback state on the UI thread; owns no playback commands or local statistics.</summary>
public sealed class LastFmPlaybackBridge(AppState state, PlaybackCoordinator coordinator, RemotePlaybackService remote,
    IpcService ipc, LastFmService service, ILogger<LastFmPlaybackBridge> logger)
{
    private readonly HashSet<Task> _tasks = [];
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _timer;
    private LastFmListenSession? _session;
    private LastFmStatus _appliedStatus = new();
    private Music? _confirmedMusic, _sessionMusic;
    private string _unknownArtist = "", _unknownAlbum = "";
    private CancellationTokenSource _nowPlaying = new();
    private long _revision = -1, _lastNowPlayingAttempt, _nextQueueAttempt, _gaplessToken;
    private bool _stopped;

    public void Start(DispatcherQueue dispatcher)
    {
        if (_dispatcher is not null || _stopped) return;
        _dispatcher = dispatcher;
        _unknownArtist = ToolUtils.GetString("UnknownArtist");
        _unknownAlbum = ToolUtils.GetString("UnknownAlbum");
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += Tick;
        coordinator.TrackStarted += Selected;
        coordinator.PlaybackConfirmed += Confirmed;
        remote.PlaybackStarted += RemoteConfirmed;
        state.Playback.PropertyChanged += PlaybackChanged;
        service.Changed += ServiceChanged;
        ApplyStatus(service.Status);
    }

    private void Selected(Music music, CancellationToken token)
    {
        // Keep a WebDAV occurrence across an interrupted connection until its resume is confirmed.
        if (!music.IsRemote || !ReferenceEquals(music, _sessionMusic)) ResetSession();
        _confirmedMusic = null;
        _timer?.Stop();
        CancelNowPlaying();
    }

    private void Confirmed(Music music, ProgressSnapshot progress) => RemoteConfirmed(music, progress, false);

    private void RemoteConfirmed(Music music, ProgressSnapshot progress, bool resumed)
    {
        if (_stopped || !ReferenceEquals(state.Playback.CurrentPlayingMusic, music)) return;
        _confirmedMusic = music;
        if (resumed && ReferenceEquals(_sessionMusic, music) && _session is not null)
        {
            _session.Rebase(progress);
            _session.NowPlayingSent = false;
        }
        else CreateSession(music, progress);
        _gaplessToken = progress.GaplessToken;
        UpdateTimer();
        Sample();
    }

    private void ServiceChanged(LastFmStatus status) => _dispatcher?.TryEnqueue(() => ApplyStatus(status));

    private void ApplyStatus(LastFmStatus status)
    {
        if (_stopped || status.Revision == _revision) return;
        _revision = status.Revision;
        bool sameAccount = status.UserName.Length != 0 && status.ApiKey == _appliedStatus.ApiKey
            && string.Equals(status.UserName, _appliedStatus.UserName, StringComparison.OrdinalIgnoreCase);
        _appliedStatus = status;
        CancelNowPlaying();
        if (!sameAccount) ResetSession();
        if (_confirmedMusic is { } music && ReferenceEquals(state.Playback.CurrentPlayingMusic, music)
            && ipc.TryGetProgressSnapshot(out var progress))
        {
            // Pausing submission or refreshing the same account must not duplicate an already
            // queued occurrence. Rebase so disabled/unauthed time is never credited on resumption.
            if (sameAccount && _session is not null)
            {
                _session.Rebase(progress);
                _session.NowPlayingSent = false;
            }
            else if (status.IsAuthenticated && status.Enabled) CreateSession(music, progress);
        }
        UpdateTimer();
    }

    private void CreateSession(Music music, ProgressSnapshot progress)
    {
        ResetSession();
        var status = service.Status;
        if (!status.IsReady || !status.Enabled || !status.IsAuthenticated) return;
        string title = music.Title.Trim();
        string artist = music.Author.Trim();
        // Unknown localized tags are presentation placeholders, not valid Last.fm metadata.
        if (title.Length == 0 || artist.Length == 0 || artist == _unknownArtist || AppData.UnknownArtists.Contains(artist)
            || title.Length > 512 || artist.Length > 512) return;
        string album = music.Album.Trim();
        if (album == _unknownAlbum || AppData.UnknownAlbums.Contains(album)) album = "";
        if (album.Length > 1024) album = album[..1024];
        int duration = (int)Math.Clamp(Math.Ceiling((progress.TotalMs > 0 ? progress.TotalMs : music.Duration.TotalMilliseconds) / 1000), 0, int.MaxValue);
        _session = new(new(title, artist, album, duration), progress, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        _sessionMusic = music;
        _gaplessToken = progress.GaplessToken;
        _revision = status.Revision;
    }

    private void PlaybackChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_stopped || args.PropertyName != nameof(PlaybackState.IsPlaying)) return;
        if (!state.Playback.IsPlaying)
        {
            CancelNowPlaying();
            if (_session is not null) _session.NowPlayingSent = false;
        }
        if (ipc.TryGetProgressSnapshot(out var progress)) _session?.Rebase(progress);
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        if (!_stopped && service.Status.Enabled && service.Status.IsAuthenticated && _session is not null
            && _confirmedMusic == state.Playback.CurrentPlayingMusic && state.Playback.IsPlaying)
            _timer?.Start();
        else _timer?.Stop();
    }

    private void Tick(DispatcherQueueTimer sender, object args) => Sample();

    internal void Sample()
    {
        if (_stopped || !service.Status.Enabled || !service.Status.IsAuthenticated || _session is not { } session || !state.Playback.IsPlaying
            || _confirmedMusic != state.Playback.CurrentPlayingMusic || !ipc.TryGetProgressSnapshot(out var progress)
            || progress.GaplessToken != _gaplessToken) return;
        session.Observe(progress);
        long now = Environment.TickCount64;
        if (progress.Playing && !session.NowPlayingSent && now >= _lastNowPlayingAttempt + 30000)
        {
            _lastNowPlayingAttempt = now;
            session.NowPlayingSent = true;
            Track(service.NowPlayingAsync(session.Track, _revision, _nowPlaying.Token));
        }
        if (session.IsQualified && !session.Enqueued && !session.EnqueuePending && now >= _nextQueueAttempt)
        {
            session.EnqueuePending = true;
            Track(EnqueueAsync(session, _revision));
        }
    }

    private async Task EnqueueAsync(LastFmListenSession session, long revision)
    {
        try { session.Enqueued = await service.EnqueueAsync(session.Track, session.StartedAt, session.Id, revision); }
        finally
        {
            session.EnqueuePending = false;
            if (ReferenceEquals(_session, session) && !session.Enqueued) _nextQueueAttempt = Environment.TickCount64 + 30000;
        }
    }

    private void Track(Task task)
    {
        _tasks.Add(task);
        _ = ObserveAsync(task);
    }

    private async Task ObserveAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { logger.LogWarning("Last.fm background operation failed: {Type}", ex.GetType().Name); }
        finally { _tasks.Remove(task); }
    }

    private void CancelNowPlaying()
    {
        _nowPlaying.Cancel();
        _nowPlaying.Dispose();
        _nowPlaying = new();
    }

    private void ResetSession()
    {
        CancelNowPlaying();
        _session = null;
        _sessionMusic = null;
        _lastNowPlayingAttempt = -30000;
        _nextQueueAttempt = 0;
    }

    public async Task StopAsync()
    {
        if (_stopped) return;
        Sample();
        _stopped = true;
        _timer?.Stop();
        if (_timer is not null) _timer.Tick -= Tick;
        coordinator.TrackStarted -= Selected;
        coordinator.PlaybackConfirmed -= Confirmed;
        remote.PlaybackStarted -= RemoteConfirmed;
        state.Playback.PropertyChanged -= PlaybackChanged;
        service.Changed -= ServiceChanged;
        service.CancelNetworkRequests();
        _nowPlaying.Cancel();
        var pending = new Task[_tasks.Count];
        _tasks.CopyTo(pending);
        try { await Task.WhenAll(pending); }
        catch (Exception) { /* Every operation has its own observer. Other shutdown steps still run. */ }
        _nowPlaying.Dispose();
        _session = null;
    }
}
