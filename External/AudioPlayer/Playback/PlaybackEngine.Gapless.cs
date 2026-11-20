using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

public sealed partial class PlaybackEngine
{
    private const double GaplessLeadTimeMs = 10_000;
    private GaplessSource? _gaplessSource;
    private GaplessPreloader? _gaplessPreloader;
    private long _currentGaplessToken;
    private long _committedGaplessEpoch;
    private GaplessRequest? _committedGaplessRequest, _lastManualTransition;
    private bool _gaplessStopping, _manualTransitionPending;

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

    // 加锁顺序为流锁后渲染锁，不能反向；分离操作等待在途拼接完成，
    // 必须先接管再使计划失效，确保已提交曲目的身份保持不变。
    private void CancelGapless()
    {
        var cancelled = _gaplessSource?.Cancel();
        AdoptGapless();
        _manualTransitionPending = false;
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
        rendered.Current.Effects?.CompleteContinuation();
        SetSession(rendered.Current);
        if (request != null && request.Token == rendered.CompletedToken)
        {
            MusicUrl = request.Path;
            _currentGaplessToken = request.Token;
            _committedGaplessEpoch = rendered.Current.TimelineEpoch;
            _committedGaplessRequest = request;
            _lastProgressSeekId = 0;
            _ipc?.GaplessTransition(request.Token, rendered.Current.TimelineEpoch, GetTimeProgress().currentMs);
        }
        _manualTransitionPending = false;
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

    // 确认响应接受的是轻量计划，解码器尚未打开。
    public bool TryQueueNext(GaplessRequest request)
    {
        lock (_streamLock)
        {
            AdoptGapless();
            if (Volatile.Read(ref _disposed) != 0 || _gaplessStopping) return false;
            if (request.Path.Length == 0) { CancelGapless(); return true; }
            if (request.Epoch != _session?.TimelineEpoch || Volatile.Read(ref _fadeBusy) != 0) return false;
            if (request == _gaplessPreloader?.Request) return true;
            if (_manualTransitionPending) return false; // 自动预载不可抢占用户的手动下一首。
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

    public TransitionNowStatus TryTransitionNow(GaplessRequest request)
    {
        lock (_streamLock)
        {
            AdoptGapless();
            if (HasCommittedTransition(request)) return TransitionNowStatus.Committed;

            bool sameManual = SameTransition(_lastManualTransition, request);
            if (Volatile.Read(ref _disposed) != 0 || _gaplessStopping || !IsPlaying || Volatile.Read(ref _fadeBusy) != 0
                || _output is not { IsFailed: false } || _currentStream != Guid.Empty || ExperimentalAtmosPassthrough
                || _session is not { Kind: RenderKind.Pcm, DecodeFailure: null } current || request.Epoch != current.TimelineEpoch
                || _gaplessSource is not { } source || request.Token <= 0 || !Path.IsPathFullyQualified(request.Path)
                || _dspSettings is not { GaplessPlayback: true, TransitionStyle: TrackTransitionStyle.Crossfade or TrackTransitionStyle.Smart } settings)
                return sameManual && _manualTransitionPending ? CancelManualTransition(request) : TransitionNowStatus.Unavailable;

            // Tokens increase within the host. A late poll must never resurrect a replaced or cancelled attempt.
            if (_lastManualTransition is { } last && last.Epoch == request.Epoch && last.Token >= request.Token)
            {
                if (!_manualTransitionPending || !sameManual) return TransitionNowStatus.Unavailable;
            }
            else
            {
                var plan = _gaplessPreloader?.Request;
                if (plan?.Token == request.Token && !SameTransition(plan, request)) return TransitionNowStatus.Unavailable;
                if (!SameTransition(plan, request) || _gaplessPreloader!.State == GaplessPreparationState.Failed)
                {
                    CancelGapless();
                    if (HasCommittedTransition(request)) return TransitionNowStatus.Committed;
                    if (_session != current || current.TimelineEpoch != request.Epoch) return TransitionNowStatus.Unavailable;
                    _gaplessPreloader ??= new(_streamLock, OpenGaplessSession, PublishGaplessSession);
                    _gaplessPreloader.SetPlan(request);
                }
                _lastManualTransition = request;
                _manualTransitionPending = true;
            }

            var preloader = _gaplessPreloader!;
            switch (preloader.State)
            {
                case GaplessPreparationState.Waiting:
                    StartGaplessPreparation(preloader, preloader.Request!, current, source);
                    return TransitionNowStatus.Preparing;
                case GaplessPreparationState.Preparing:
                    return TransitionNowStatus.Preparing;
                case GaplessPreparationState.Ready:
                    var status = source.RequestTransitionNow(request.Epoch, request.Token, settings.TransitionStyle, settings.CrossfadeSeconds);
                    if (status != TransitionNowStatus.Unavailable) return status;
                    break;
            }
            return CancelManualTransition(request);
        }
    }

    private static bool SameTransition(GaplessRequest? plan, GaplessRequest request) =>
        plan != null && plan.Epoch == request.Epoch && plan.Token == request.Token && string.Equals(plan.Path, request.Path, StringComparison.Ordinal);

    private bool HasCommittedTransition(GaplessRequest request) =>
        request.Token == _currentGaplessToken && _session?.TimelineEpoch == _committedGaplessEpoch
        && SameTransition(_committedGaplessRequest, request);

    private TransitionNowStatus CancelManualTransition(GaplessRequest request)
    {
        CancelGapless();
        return HasCommittedTransition(request) ? TransitionNowStatus.Committed : TransitionNowStatus.Unavailable;
    }

    // 由现有控制看门狗调用；窗口外不探测文件，也不产生额外分配。
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
            var style = ResolveTransitionStyle(request, _dspSettings);
            double leadTimeMs = GaplessLeadTimeMs;
            if (style is TrackTransitionStyle.Crossfade or TrackTransitionStyle.Smart)
            {
                leadTimeMs = Math.Max(leadTimeMs, (_dspSettings.CrossfadeSeconds + 3) * 1000);
                position = current.CurrentMs;
            }
            if (duration > 0 && (duration - position) / current.PlaybackRate > leadTimeMs) return;
            StartGaplessPreparation(preloader, request, current, source);
        }
    }

    private void StartGaplessPreparation(GaplessPreloader preloader, GaplessRequest request, Session current, GaplessSource source)
        => preloader.Start(new(request, current, source, DsdPcmFreq, DsdGain, Latency,
            ExperimentalSurround51, EffectivePlaybackRate, IsDopEnabled && !IsSharedMode(OutputMode), EffectiveEnhancement,
            EnhancementMixRate(), EffectiveResampleMode, EffectiveResampleTarget, _dspSettings?.TransitionStyle == TrackTransitionStyle.Smart));

    private Session? OpenGaplessSession(GaplessPreparation preparation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (preparation.BitstreamEnabled && IsRawDsdContainer(preparation.Request.Path)) return null;
        // 解码器探测在此工作线程执行，包括慢速 UNC 路径上的本地文件。
        return Session.Open(this, preparation.Request.Path, RenderKind.Pcm,
            preparation.DsdRate, preparation.DsdGain, preparation.Latency,
            maxChannels: preparation.Current.Channels <= 2 ? 2 : null,
            experimentalSurround51: preparation.Surround, cancellationToken: token,
            playbackRate: preparation.PlaybackRate, enhancement: preparation.Enhancement,
            enhancementDeviceRate: preparation.EnhancementDeviceRate, resampleMode: preparation.ResampleMode,
            resampleTargetRate: preparation.ResampleTargetRate, measureTransitionIntro: preparation.MeasureTransitionIntro);
    }

    // 由预加载器在流锁内调用，避免渲染或释放与所有权转移发生竞争。
    private bool PublishGaplessSession(GaplessPreparation preparation, Session next)
    {
        if (Volatile.Read(ref _disposed) != 0 || _session != preparation.Current
            || _gaplessSource != preparation.Source) return false;
        next.ConfigureDsp(ResolveDsp(_output?.DeviceId));
        next.Eq.Configure(next.SampleRate, IsEqualizerEnabled, EqGains, EqQ);
        var style = _manualTransitionPending ? _dspSettings?.TransitionStyle ?? TrackTransitionStyle.Gapless
            : ResolveTransitionStyle(preparation.Request, _dspSettings);
        if (_manualTransitionPending && style == TrackTransitionStyle.Gapless) return false;
        return preparation.Source.Queue(next, preparation.Request.Token, style,
            _dspSettings?.CrossfadeSeconds ?? DspSettings.DefaultCrossfadeSeconds, _manualTransitionPending);
    }

    private static TrackTransitionStyle ResolveTransitionStyle(GaplessRequest request, DspSettings? settings) =>
        settings?.TransitionStyle switch
        {
            TrackTransitionStyle.Crossfade => TrackTransitionStyle.Crossfade,
            TrackTransitionStyle.Smart when !request.PreserveAlbumBoundary => TrackTransitionStyle.Smart,
            _ => TrackTransitionStyle.Gapless,
        };

    // 相对于渲染边界的拼接操作，原子地快照两个借用引用。
    // 在渲染锁外发布目标时，由流锁防止对象被释放。
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
        if (!settings.IsEnabled || !settings.ConvolutionEnabled) PreparedCorrectionCache.Clear();
        RetireInactiveConvolution();
    }

    private void RetireInactiveConvolution()
    {
        if (IsPlaying || _pauseFadeActive) return;
        if (_gaplessSource is { } source) source.RetireInactiveConvolution();
        // 所有实际 PCM 输出均经 OutputSource 包装；无包装且无输出的会话尚未开始渲染。
        else if (_output == null) _session?.Effects?.RetireInactiveConvolution();
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
