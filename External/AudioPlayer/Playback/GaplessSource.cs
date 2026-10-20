namespace AudioPlayer.Playback;

/// <summary>稳定的输出音源：在准确的 PCM 帧边界拼接，无需停止设备。
/// 引擎持有会话；此锁防止取消或释放与在途渲染同时发生。
/// 音频线程不执行文件 I/O、创建任务、发送通知或释放资源。</summary>
internal sealed class GaplessSource(Session current) : IRenderSource
{
    private readonly object _gate = new();
    private Session _current = current;
    private Session? _next;
    private Session? _outgoing, _retired;
    private AutoMixPlan _mixPlan;
    private double[]? _mixScratch;
    private long _overlapFrames, _mixedFrames;
    private double _gainA = 1, _gainB, _sinStep, _cosStep, _limiterGain = 1;
    private readonly double _limiterRelease = 1 - Math.Exp(-1.0 / (0.12 * current.SampleRate));
    private double _lastLeft, _lastRight;
    private int _recoveryRemaining;
    private long _token, _completedToken, _submitted;
    private readonly int _sampleRate = current.SampleRate, _channels = current.Channels;
    private readonly uint _channelMask = current.ChannelMask;
    internal Session Current => Volatile.Read(ref _current);
    internal Session? Pending => Volatile.Read(ref _next);
    internal Session? Outgoing => Volatile.Read(ref _outgoing);
    internal bool IsTransitioning { get { lock (_gate) return _outgoing != null; } }
    internal long CompletedToken => Volatile.Read(ref _completedToken);
    public RenderKind Kind => RenderKind.Pcm;
    public int SampleRate => _sampleRate;
    public int Channels => _channels;
    public uint ChannelMask => _channelMask;
    public long SubmittedFrames => Interlocked.Read(ref _submitted);

    internal (Session Current, Session? Pending, long CompletedToken) Snapshot()
    {
        lock (_gate) return (_current, _next, _completedToken);
    }

    internal bool Queue(Session next, long token)
    {
        lock (_gate)
        {
            if (_next != null || _outgoing != null || _retired != null || next.Kind != Kind || next.SampleRate != SampleRate
                || next.Channels != Channels || next.ChannelMask != ChannelMask) return false;
            _next = next;
            _token = token;
            _mixPlan = Channels == 2 ? next.TransitionPlan : default;
            if (_mixPlan.IsMix)
            {
                // Owned by this stable source, allocated only on the preloader/control thread.
                _mixScratch ??= new double[512 * 2];
                _overlapFrames = Math.Max(1, (long)_mixPlan.OverlapMs * SampleRate / 1000);
                double step = Math.PI / (2 * _overlapFrames);
                _sinStep = Math.Sin(step);
                _cosStep = Math.Cos(step);
            }
            return true;
        }
    }

    internal Session? Cancel()
    {
        lock (_gate)
        {
            var next = _next;
            _next = null;
            _mixPlan = default;
            if (_outgoing != null)
            {
                _retired = _outgoing;
                _outgoing = null;
                // Preserve sample continuity when an explicit pause/seek/skip takes over an audible mix.
                _recoveryRemaining = Math.Max(1, SampleRate / 200);
            }
            return next;
        }
    }

    internal bool Retains(Session session)
    {
        lock (_gate) return _outgoing == session || _retired == session;
    }

    internal Session? TakeRetired()
    {
        lock (_gate)
        {
            var retired = _retired;
            _retired = null;
            return retired;
        }
    }

    private static bool Ready(Session session) => session.DecodeFailure == null
        && (session.ReadyFrames >= session.InitialBufferFrames || session.InputEnded && session.ReadyFrames > 0);

    private void StartMix(Session incoming)
    {
        _outgoing = _current;
        _current = incoming;
        _next = null;
        _mixedFrames = 0;
        _gainA = 1;
        _gainB = 0;
        Volatile.Write(ref _completedToken, _token);
    }

    public void FillPcm(Span<double> buffer, int frames)
    {
        lock (_gate)
        {
            int offset = 0, rendered = 0;
            bool mixed = false;
            while (offset < frames)
            {
                if (_outgoing != null)
                {
                    int count = (int)Math.Min(Math.Min(512, frames - offset), _overlapFrames - _mixedFrames);
                    var destination = buffer.Slice(offset * Channels, count * Channels);
                    var scratch = _mixScratch.AsSpan(0, count * Channels);
                    int a = _outgoing.RenderPcm(scratch, count, captureFft: false);
                    int b = _current.RenderPcm(destination, count, captureFft: false);
                    for (int i = 0; i < count; i++)
                    {
                        destination[i * 2] = scratch[i * 2] * _gainA + destination[i * 2] * _gainB;
                        destination[i * 2 + 1] = scratch[i * 2 + 1] * _gainA + destination[i * 2 + 1] * _gainB;
                        double gainA = _gainA * _cosStep - _gainB * _sinStep;
                        _gainB = _gainB * _cosStep + _gainA * _sinStep;
                        _gainA = gainA;
                    }
                    _mixedFrames += count;
                    offset += count;
                    rendered += Math.Max(a, b);
                    mixed = true;
                    if (_mixedFrames >= _overlapFrames)
                    {
                        _retired = _outgoing;
                        _outgoing = null;
                        _mixPlan = default;
                    }
                    continue;
                }
                var next = _next;
                bool ready = next != null && Ready(next);
                long until = _mixPlan.IsMix ? _current.FramesUntilPosition(_mixPlan.ExitMs) : long.MaxValue;
                if (ready && until <= 0)
                {
                    StartMix(next!);
                    continue;
                }
                int take = ready && _mixPlan.IsMix ? (int)Math.Min(frames - offset, until) : frames - offset;
                int read = _current.RenderPcm(buffer.Slice(offset * Channels, take * Channels), take, captureFft: false);
                offset += read;
                rendered += read;
                if (read < take)
                {
                    if (_current.IsDrained && ready)
                    {
                        next!.ContinueDspFrom(_current);
                        _next = null;
                        _current = next;
                        _mixPlan = default;
                        Volatile.Write(ref _completedToken, _token);
                        continue;
                    }
                    break;
                }
            }
            // Mixed blocks can contain underrun silence followed by audio from a later block.
            if (offset < frames) buffer[(offset * Channels)..(frames * Channels)].Clear();
            if (Channels == 2 && (mixed || _limiterGain < 0.999999 || _recoveryRemaining > 0))
                LimitAndRecover(buffer, frames);
            if (Channels == 2 && frames > 0)
            {
                _lastLeft = buffer[(frames - 1) * 2];
                _lastRight = buffer[(frames - 1) * 2 + 1];
            }
            if (rendered > 0) _current.CapturePcm(buffer, rendered);
            Interlocked.Add(ref _submitted, rendered);
        }
    }

    private void LimitAndRecover(Span<double> buffer, int frames)
    {
        int recoveryFrames = Math.Max(1, SampleRate / 200);
        for (int i = 0; i < frames; i++)
        {
            double left = double.IsFinite(buffer[i * 2]) ? buffer[i * 2] : 0;
            double right = double.IsFinite(buffer[i * 2 + 1]) ? buffer[i * 2 + 1] : 0;
            if (_recoveryRemaining > 0)
            {
                double t = 1 - (--_recoveryRemaining / (double)recoveryFrames);
                left = _lastLeft + (left - _lastLeft) * t;
                right = _lastRight + (right - _lastRight) * t;
            }
            double peak = Math.Max(Math.Abs(left), Math.Abs(right));
            double target = peak > 0.98 ? 0.98 / peak : 1;
            _limiterGain = target < _limiterGain ? target
                : Math.Min(target, _limiterGain + (1 - _limiterGain) * _limiterRelease);
            buffer[i * 2] = left * _limiterGain;
            buffer[i * 2 + 1] = right * _limiterGain;
        }
    }

    public void FillDop(Span<uint> buffer, int frames) => throw new NotSupportedException();
    public void FillDsdBytes(Span<byte> buffer, int byteFrames) => throw new NotSupportedException();
}
