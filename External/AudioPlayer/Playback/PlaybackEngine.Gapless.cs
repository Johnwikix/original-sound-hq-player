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

    // 加锁顺序为流锁后渲染锁，不能反向；分离操作等待在途拼接完成，
    // 必须先接管再使计划失效，确保已提交曲目的身份保持不变。
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

    // 确认响应接受的是轻量计划，解码器尚未打开。
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
            if (duration > 0 && (duration - position) / current.PlaybackRate > GaplessLeadTimeMs) return;
            preloader.Start(new(request, current, source, DsdPcmFreq, DsdGain, Latency,
                ExperimentalSurround51, EffectivePlaybackRate, IsDopEnabled && !IsSharedMode(OutputMode), EffectiveEnhancement, EnhancementMixRate(), EffectiveResampleMode, EffectiveResampleTarget));
        }
    }

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
            enhancementDeviceRate: preparation.EnhancementDeviceRate, resampleMode: preparation.ResampleMode, resampleTargetRate: preparation.ResampleTargetRate);
    }

    // 由预加载器在流锁内调用，避免渲染或释放与所有权转移发生竞争。
    private bool PublishGaplessSession(GaplessPreparation preparation, Session next)
    {
        if (Volatile.Read(ref _disposed) != 0 || _session != preparation.Current
            || _gaplessSource != preparation.Source) return false;
        next.ConfigureDsp(ResolveDsp(_output?.DeviceId));
        next.Eq.Configure(next.SampleRate, IsEqualizerEnabled, EqGains, EqQ);
        next.Gain?.SetImmediately(GainVolumeTarget);
        return preparation.Source.Queue(next, preparation.Request.Token);
    }

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
