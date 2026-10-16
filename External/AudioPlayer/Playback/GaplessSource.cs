namespace AudioPlayer.Playback;

/// <summary>稳定的输出音源：在准确的 PCM 帧边界拼接，无需停止设备。
/// 引擎持有会话；此锁防止取消或释放与在途渲染同时发生。
/// 音频线程不执行文件 I/O、创建任务、发送通知或释放资源。</summary>
internal sealed class GaplessSource(Session current) : IRenderSource
{
    private readonly object _gate = new();
    private Session _current = current;
    private Session? _next;
    private long _token, _completedToken, _submitted;
    private readonly int _sampleRate = current.SampleRate, _channels = current.Channels;
    private readonly uint _channelMask = current.ChannelMask;
    internal Session Current => Volatile.Read(ref _current);
    internal Session? Pending => Volatile.Read(ref _next);
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
            if (_next != null || next.Kind != Kind || next.SampleRate != SampleRate
                || next.Channels != Channels || next.ChannelMask != ChannelMask) return false;
            _next = next;
            _token = token;
            return true;
        }
    }

    internal Session? Cancel()
    {
        lock (_gate)
        {
            var next = _next;
            _next = null;
            return next;
        }
    }

    public void FillPcm(Span<double> buffer, int frames)
    {
        lock (_gate)
        {
            int rendered = _current.RenderPcm(buffer, frames);
            if (rendered < frames && _current.IsDrained && _next is { DecodeFailure: null } next
                && (next.ReadyFrames >= next.InitialBufferFrames || next.InputEnded && next.ReadyFrames > 0))
            {
                next.ContinueDspFrom(_current);
                _next = null;
                _current = next;
                Volatile.Write(ref _completedToken, _token);
                rendered += next.RenderPcm(buffer[(rendered * Channels)..], frames - rendered);
            }
            Interlocked.Add(ref _submitted, rendered);
        }
    }

    public void FillDop(Span<uint> buffer, int frames) => throw new NotSupportedException();
    public void FillDsdBytes(Span<byte> buffer, int byteFrames) => throw new NotSupportedException();
}
