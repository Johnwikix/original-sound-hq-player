using BassPlayerIpc.Shared;

namespace AudioPlayer.Playback;

/// <summary>稳定的输出音源：在准确的 PCM 帧边界拼接，无需停止设备。
/// 引擎持有会话；此锁防止取消或释放与在途渲染同时发生。
/// 音频线程不执行文件 I/O、创建任务、发送通知或释放资源。</summary>
internal sealed class GaplessSource(Session current) : IRenderSource
{
    private const int MixBlockFrames = 512;
    private readonly object _gate = new();
    private Session _current = current;
    private Session? _next;
    private long _token, _completedToken, _submitted;
    private readonly int _sampleRate = current.SampleRate, _channels = current.Channels;
    private readonly int _recoveryDuration = Math.Max(1, current.SampleRate / 50);
    private readonly uint _channelMask = current.ChannelMask;
    private double[]? _mixBuffer;
    private TrackTransitionStyle _style;
    private int _overlapLimit, _overlapRemaining, _endGuard, _recoveryRemaining;
    private double _incomingWeight, _recoveryStartWeight;
    private long _recoveryEpoch;
    private bool _rejectPending, _manualRequested, _manualOverlap, _overlapActive;
    internal Session Current => Volatile.Read(ref _current);
    internal Session? Pending => Volatile.Read(ref _next);
    internal long CompletedToken => Volatile.Read(ref _completedToken);
    public RenderKind Kind => RenderKind.Pcm;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public bool DitherEnabled => Current.DitherEnabled;
    public uint ChannelMask => _channelMask;
    public long SubmittedFrames => Interlocked.Read(ref _submitted);

    internal (Session Current, Session? Pending, long CompletedToken) Snapshot()
    {
        lock (_gate) return (_current, _next, _completedToken);
    }

    internal bool TryCaptureOverlap(Session from, out Session? incoming, out long token, out long positionMs)
    {
        if (Volatile.Read(ref _overlapActive))
        {
            lock (_gate)
            {
                if (_overlapActive && _current == from && _next is { } next)
                {
                    incoming = next;
                    token = _token;
                    positionMs = next.CurrentMs;
                    return true;
                }
            }
        }
        incoming = null;
        token = positionMs = 0;
        return false;
    }

    internal bool Queue(Session next, long token, TrackTransitionStyle style = TrackTransitionStyle.Gapless,
        int durationMs = DspSettings.DefaultCrossfadeMilliseconds, bool transitionNow = false)
    {
        lock (_gate)
        {
            if (_next != null || next.Kind != Kind || next.SampleRate != SampleRate
                || next.Channels != Channels || next.ChannelMask != ChannelMask) return false;
            SetTransition(style, durationMs);
            _manualRequested = transitionNow;
            next.ShareOutputGain(_current);
            _next = next;
            _token = token;
            return true;
        }
    }

    internal TransitionNowStatus RequestTransitionNow(long epoch, long token, TrackTransitionStyle style, int durationMs)
    {
        lock (_gate)
        {
            if (_current.TimelineEpoch != epoch || _token != token || _next is not { } next || _rejectPending
                || _current.DecodeFailure != null || next.DecodeFailure != null || _current.TailStarted)
                return TransitionNowStatus.Unavailable;
            if (_incomingWeight > 0)
            {
                // A natural overlap to the same track is already audible. Keep its envelope and render position.
                _manualRequested = true;
                return TransitionNowStatus.Started;
            }
            if (!_manualRequested)
            {
                SetTransition(style, durationMs);
                _manualRequested = true;
            }
            // Decoder EOF provides an exact frame count for a short or unlabelled track.
            return GetOverlapLength(next, _current.PcmInputStatus, next.PcmInputStatus, out _) >= MinimumOverlapFrames
                ? TransitionNowStatus.Preparing : TransitionNowStatus.Unavailable;
        }
    }

    internal void UpdateTransition(TrackTransitionStyle style, int durationMs)
    {
        lock (_gate)
        {
            if (_next != null && !_manualRequested) SetTransition(style, durationMs);
        }
    }

    private void SetTransition(TrackTransitionStyle style, int durationMs)
    {
        _style = style;
        _overlapLimit = style is TrackTransitionStyle.Crossfade or TrackTransitionStyle.Smart
            ? (int)((long)DspSettings.NormalizeCrossfadeMilliseconds(durationMs) * SampleRate / 1000) : 0;
        if (_overlapLimit > 0) _mixBuffer ??= new double[(MixBlockFrames + 1) * Channels];
    }

    internal Session? Cancel()
    {
        lock (_gate)
        {
            var next = _next;
            AbortOverlap();
            _rejectPending = false;
            _manualRequested = false;
            _next = null;
            return next;
        }
    }

    /// <summary>与渲染及曲目交接使用同一屏障；控制端必须已暂停输出。</summary>
    internal void RetireInactiveConvolution()
    {
        lock (_gate)
        {
            _current.Effects?.RetireInactiveConvolution();
            _next?.Effects?.RetireInactiveConvolution();
        }
    }

    public void FillPcm(Span<double> buffer, int frames)
    {
        lock (_gate)
        {
            int rendered = RenderRecovery(buffer, frames);
            if (rendered < frames && _recoveryRemaining == 0 && !_rejectPending && _next is { } next)
            {
                if (_overlapRemaining > 0 || _overlapLimit > 0 && !_current.TailStarted && CanJoin(next))
                    rendered += RenderOverlap(next, buffer[(rendered * Channels)..], frames - rendered);
            }
            if (rendered < frames) rendered += RenderRecovery(buffer[(rendered * Channels)..], frames - rendered);
            if (rendered < frames) rendered += RenderRemainder(buffer[(rendered * Channels)..], frames - rendered);
            Interlocked.Add(ref _submitted, rendered);
        }
    }

    private int RenderRemainder(Span<double> buffer, int frames)
    {
        bool join = !_manualRequested && !_current.TailStarted && CanJoin(_next);
        int rendered = _current.RenderPcm(buffer, frames, drainTail: !join);
        if (!_manualRequested && rendered < frames && (join ? _current.PcmInputDrained : _current.IsDrained)
            && _next is { } next && CanJoin(next))
        {
            Promote(next, preserveInputGain: false);
            rendered += next.RenderPcm(buffer[(rendered * Channels)..], frames - rendered);
        }
        return rendered;
    }

    private bool CanJoin(Session? next)
    {
        if (_rejectPending || next is not { DecodeFailure: null, DspReady: true }) return false;
        var (ready, ended) = next.PcmInputStatus;
        return ready > 0 && (ready >= next.InitialBufferFrames || ended);
    }

    private int RenderOverlap(Session next, Span<double> buffer, int frames)
    {
        int rendered = 0;
        if (_overlapRemaining == 0)
        {
            int prefix = GetOverlapStart(next, frames, out int overlap);
            if (overlap == 0) return 0;
            if (prefix > 0)
            {
                rendered = _current.RenderPcm(buffer, prefix, drainTail: false);
                if (rendered < prefix) return rendered;
            }
            _overlapRemaining = overlap;
            _manualOverlap = _manualRequested;
            _endGuard = Math.Min(overlap / 4, SampleRate / 8);
        }

        while (rendered < frames)
        {
            var (outgoingReady, outgoingEnded) = _current.PcmInputStatus;
            var (incomingReady, incomingEnded) = next.PcmInputStatus;
            if (next.DecodeFailure != null || !next.DspReady || incomingReady == 0
                || !_manualOverlap && outgoingEnded && outgoingReady > _overlapRemaining + SampleRate / 20
                || !outgoingEnded && (outgoingReady == 0 || !_manualOverlap && _overlapRemaining <= _endGuard))
            {
                AbortOverlap();
                break;
            }

            // A manual fade may end mid-track. At EOF it can only get shorter.
            if (outgoingEnded) _overlapRemaining = _manualOverlap ? Math.Min(_overlapRemaining, outgoingReady) : outgoingReady;
            if (incomingEnded && incomingReady < _overlapRemaining)
            {
                AbortOverlap();
                break;
            }
            if (_overlapRemaining == 0)
            {
                Promote(next, preserveInputGain: _incomingWeight > 0);
                break;
            }

            int take = Math.Min(frames - rendered, MixBlockFrames);
            take = Math.Min(take, Math.Min(outgoingReady, incomingReady));
            take = Math.Min(take, outgoingEnded || _manualOverlap ? _overlapRemaining : _overlapRemaining - _endGuard);
            var output = buffer.Slice(rendered * Channels, take * Channels);
            RenderMix(next, output, take);
            rendered += take;
            _overlapRemaining -= take;
            if (_overlapRemaining == 0)
            {
                Promote(next, preserveInputGain: true);
                break;
            }
        }
        return rendered;
    }

    private int GetOverlapStart(Session next, int frames, out int overlap)
    {
        overlap = 0;
        var outgoing = _current.PcmInputStatus;
        var incoming = next.PcmInputStatus;
        if (outgoing.ReadyFrames == 0) return 0;
        int duration = GetOverlapLength(next, outgoing, incoming, out long remaining);
        if (duration < MinimumOverlapFrames) return 0;
        long prefix = _manualRequested ? 0 : remaining - duration;
        if (prefix >= frames || prefix >= outgoing.ReadyFrames) return 0;
        // Buffer only enough for this pull. Both decoder threads continue filling their rings in parallel.
        int needed = (int)Math.Min(duration, frames - prefix);
        if (incoming.ReadyFrames < needed) return 0;
        overlap = duration;
        return (int)prefix;
    }

    private int MinimumOverlapFrames => Math.Max(2, SampleRate * 80 / 1000);

    private int GetOverlapLength(Session next, (int ReadyFrames, bool Ended) outgoing, (int ReadyFrames, bool Ended) incoming,
        out long remaining)
    {
        remaining = 0;
        if (!outgoing.Ended && _current.TotalMs <= 0 || !incoming.Ended && next.TotalMs <= 0) return 0;
        long played = Volatile.Read(ref _current.AnchorFrames) + _current.FramesPlayed;
        long outgoingLength = outgoing.Ended ? played + outgoing.ReadyFrames : _current.MsToFrames(_current.TotalMs);
        long incomingLength = incoming.Ended ? incoming.ReadyFrames : next.MsToFrames(next.TotalMs);
        remaining = outgoing.Ended ? outgoing.ReadyFrames : Math.Max(outgoing.ReadyFrames, outgoingLength - played);
        int preferred = _overlapLimit;
        int lengthDivisor = 2;
        if (_style == TrackTransitionStyle.Smart)
        {
            lengthDivisor = 4;
            // A quiet intro can enter earlier; a loud attack gets a shorter blend.
            double energy = next.IntroMeanSquare;
            preferred = energy <= 0.00025 ? preferred : energy >= 0.01 ? preferred / 2 : preferred * 3 / 4;
        }
        preferred = (int)Math.Min(preferred, Math.Min(outgoingLength, incomingLength) / lengthDivisor);
        return (int)Math.Min(remaining, preferred);
    }

    private void RenderMix(Session next, Span<double> output, int frames)
    {
        var incoming = _mixBuffer.AsSpan(0, output.Length);
        // The render lock excludes seeks and retirement; each ring has only one consumer.
        _current.ReadPcm(output, frames);
        next.ReadPcm(incoming, frames);
        _current.ApplyPcmInputGain(output, frames);
        next.ApplyPcmInputGain(incoming, frames);
        double weight = _incomingWeight;
        bool started = weight == 0;
        double step = (1 - weight) / _overlapRemaining;
        for (int offset = 0; offset < output.Length; offset += Channels)
        {
            weight = Math.Min(1, weight + step);
            for (int ch = 0; ch < Channels; ch++)
            {
                int sample = offset + ch;
                output[sample] = output[sample] * (1 - weight) + incoming[sample] * weight;
            }
        }
        _incomingWeight = weight;
        // The final slot retains one post-normalization frame after the standby session is detached.
        incoming[^Channels..].CopyTo(_mixBuffer.AsSpan(MixBlockFrames * Channels));
        _current.ProcessPcm(output, frames, inputGainApplied: true);
        if (started) Volatile.Write(ref _overlapActive, true);
    }

    private void AbortOverlap()
    {
        _overlapRemaining = 0;
        _rejectPending |= _manualRequested;
        if (_incomingWeight == 0) return;
        // A partially consumed intro cannot be handed off as an ordinary splice.
        _rejectPending = true;
        _recoveryStartWeight = _incomingWeight;
        _recoveryRemaining = _recoveryDuration;
        _recoveryEpoch = _current.TimelineEpoch;
        _incomingWeight = 0;
        Volatile.Write(ref _overlapActive, false);
    }

    private int RenderRecovery(Span<double> buffer, int frames)
    {
        if (_recoveryRemaining <= 0 || frames <= 0) return 0;
        if (_recoveryEpoch != _current.TimelineEpoch)
        {
            _recoveryRemaining = 0;
            return 0;
        }
        int take = Math.Min(frames, _recoveryRemaining);
        _current.ReadPcm(buffer, take);
        _current.ApplyPcmInputGain(buffer, take);
        var lastIncoming = _mixBuffer.AsSpan(MixBlockFrames * Channels);
        double step = _recoveryStartWeight / _recoveryDuration;
        // Finish the decay even if the outgoing ring is empty. Its read pads the unavailable frames with silence.
        for (int frame = 0; frame < take; frame++)
        {
            double weight = --_recoveryRemaining * step;
            for (int ch = 0; ch < Channels; ch++)
            {
                int sample = frame * Channels + ch;
                buffer[sample] = buffer[sample] * (1 - weight) + lastIncoming[ch] * weight;
            }
        }
        _current.ProcessPcm(buffer, take, inputGainApplied: true);
        return take;
    }

    private void Promote(Session next, bool preserveInputGain)
    {
        next.ContinueDspFrom(_current, preserveInputGain);
        _next = null;
        _manualRequested = false;
        _overlapRemaining = _recoveryRemaining = 0;
        _incomingWeight = 0;
        Volatile.Write(ref _overlapActive, false);
        Volatile.Write(ref _current, next);
        Volatile.Write(ref _completedToken, _token);
    }

    public void FillDop(Span<uint> buffer, int frames) => throw new NotSupportedException();
    public void FillDsdBytes(Span<byte> buffer, int byteFrames) => throw new NotSupportedException();
}
