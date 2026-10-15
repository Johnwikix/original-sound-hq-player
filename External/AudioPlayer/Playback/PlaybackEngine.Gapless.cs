using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    private const double GaplessLeadTimeMs = 10_000;
    private GaplessSource? _gaplessSource;
    private GaplessPreloader? _gaplessPreloader;
    private long _currentGaplessToken;
    private bool _gaplessStopping;

    private double EffectivePlaybackRate => _dspSettings is { IsEnabled: true } dsp ? dsp.PlaybackRate : 1;

    private IRenderSource OutputSource(Session session)
    {
        if (session.Kind != RenderKind.Pcm) return session;
        if (_gaplessSource?.Current != session)
        {
            CancelGapless();
            _gaplessSource = new(session);
        }
        return _gaplessSource;
    }

    // Stream lock -> render gate, never the reverse. Detach drains an in-flight splice;
    // adoption must precede invalidating its plan so a committed track keeps its identity.
    private void CancelGapless()
    {
        var cancelled = _gaplessSource?.Cancel();
        AdoptGapless();
        _gaplessPreloader?.Cancel();
        RetireGaplessSession(cancelled);
    }

    public void SynchronizeGapless()
    {
        lock (_streamLock) AdoptGapless();
    }

    public (long Token, long Epoch) GetGaplessIdentity()
    {
        lock (_streamLock)
        {
            AdoptGapless();
            return (_currentGaplessToken, _session?.TimelineEpoch ?? 0);
        }
    }

    private void AdoptGapless()
    {
        if (_gaplessSource is not { } source) return;
        var rendered = source.Snapshot();
        if (rendered.Current == _session || rendered.CompletedToken == 0) return;
        var old = _session;
        var request = _gaplessPreloader?.Request;
        SetSession(rendered.Current);
        if (request != null && request.Token == rendered.CompletedToken)
        {
            MusicUrl = request.Path;
            _currentGaplessToken = request.Token;
            _lastProgressSeekId = 0;
            _ipc?.GaplessTransition(request.Token, rendered.Current.TimelineEpoch);
        }
        _gaplessPreloader?.Cancel();
        RetireGaplessSession(old);
    }

    private void RetireGaplessSession(Session? session)
    {
        if (session == null) return;
        if (_gaplessPreloader != null) _gaplessPreloader.Retire(session);
        else session.Dispose();
    }

    public void QueueNext(GaplessRequest request) => TryQueueNext(request);

    // Acknowledgement accepts a lightweight plan, not an already opened decoder.
    public bool TryQueueNext(GaplessRequest request)
    {
        lock (_streamLock)
        {
            AdoptGapless();
            if (Volatile.Read(ref _disposed) != 0 || _gaplessStopping) return false;
            if (request.Path.Length == 0) { CancelGapless(); return true; }
            if (request.Epoch != _session?.TimelineEpoch) return false;
            if (request == _gaplessPreloader?.Request) return true;
            CancelGapless();
            if (!IsPlaying || _dspSettings?.GaplessPlayback != true
                || _session is not { Kind: RenderKind.Pcm } || _gaplessSource == null
                || ExperimentalAtmosPassthrough || _currentStream != Guid.Empty
                || !Path.IsPathFullyQualified(request.Path)) return false;
            _gaplessPreloader ??= new(_streamLock, OpenGaplessSession, PublishGaplessSession);
            _gaplessPreloader.SetPlan(request);
            PrepareGaplessIfDue();
            return true;
        }
    }

    // Called by the existing control watchdog. No file probes or allocations outside the window.
    private void PrepareGaplessIfDue()
    {
        lock (_streamLock)
        {
            AdoptGapless();
            if (Volatile.Read(ref _disposed) != 0 || _gaplessStopping || !IsPlaying || _dspSettings?.GaplessPlayback != true
                || _gaplessPreloader is not { State: GaplessPreparationState.Waiting, Request: { } request } preloader
                || _session is not { Kind: RenderKind.Pcm } current || _gaplessSource is not { } source) return;
            if (request.Epoch != current.TimelineEpoch) { CancelGapless(); return; }
            var (position, duration) = GetTimeProgress();
            if (duration > 0 && (duration - position) / current.PlaybackRate > GaplessLeadTimeMs) return;
            preloader.Start(new(request, current, source, DsdPcmFreq, DsdGain, Latency,
                ExperimentalSurround51, EffectivePlaybackRate, IsDopEnabled && !IsSharedMode(OutputMode), SpectralEnhancement, EnhancementMixRate()));
        }
    }

    private Session? OpenGaplessSession(GaplessPreparation preparation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (preparation.BitstreamEnabled && IsRawDsdContainer(preparation.Request.Path)) return null;
        // Decoder probing belongs on this worker, including local files on slow UNC paths.
        return Session.Open(this, preparation.Request.Path, RenderKind.Pcm,
            preparation.DsdRate, preparation.DsdGain, preparation.Latency,
            maxChannels: preparation.Current.Channels <= 2 ? 2 : null,
            experimentalSurround51: preparation.Surround, cancellationToken: token,
            playbackRate: preparation.PlaybackRate, enhancement: preparation.Enhancement,
            enhancementDeviceRate: preparation.EnhancementDeviceRate);
    }

    // Invoked by the preloader under the stream lock; no rendering/disposal can race ownership transfer.
    private bool PublishGaplessSession(GaplessPreparation preparation, Session next)
    {
        if (Volatile.Read(ref _disposed) != 0 || _session != preparation.Current
            || _gaplessSource != preparation.Source) return false;
        next.ConfigureDsp(ResolveDsp(_output?.DeviceId));
        next.Eq.Configure(next.SampleRate, IsEqualizerEnabled, EqGains, EqQ);
        next.Gain?.SetImmediately(GainVolumeTarget);
        return preparation.Source.Queue(next, preparation.Request.Token);
    }

    // Snapshot both borrowed references atomically with respect to a render-boundary splice.
    // Stream lock prevents disposal while targets are published outside the render gate.
    private void ApplyDspToSessions()
    {
        var settings = ResolveDsp(_output?.DeviceId);
        if (_gaplessSource is { } source)
        {
            var rendered = source.Snapshot();
            rendered.Current.ConfigureDsp(settings);
            rendered.Pending?.ConfigureDsp(settings);
        }
        else _session?.ConfigureDsp(settings);
    }

    public Task StopGaplessAsync()
    {
        lock (_streamLock)
        {
            _gaplessStopping = true;
            CancelGapless();
            return _gaplessPreloader?.StopAsync() ?? Task.CompletedTask;
        }
    }
}
