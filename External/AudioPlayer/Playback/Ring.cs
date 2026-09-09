using System.Diagnostics;

namespace AudioPlayer.Playback;

/// <summary>
/// ECHO 语义的单生产者/单消费者帧环形缓冲：
/// 生产者满时 4ms 步进等待；消费者在预缓冲门槛未到或欠载时输出静音并计数；
/// 计数器（FramesPlayed/欠载）只在 beginSession/Reset 时清零。
/// </summary>
internal abstract class FrameRingBase<T> where T : unmanaged
{
    private readonly T[] _buffer;
    private readonly object _gate = new();
    private long _head; // 总写入帧数
    private long _tail; // 总读取帧数

    private readonly int _prebufferFrames;
    private readonly int _prebufferTimeoutMs;
    private long _prebufferDeadlineTicks;
    private bool _prebuffering;
    private long _epoch; // 会话代数：BeginSession 递增，Push 据此丢弃 seek 前的旧数据

    protected readonly int Channels;
    private bool _sessionHasAudio;

    public volatile bool InputEnded;
    private long _framesPlayed;
    private long _underrunCallbacks;
    private long _underrunFrames;

    protected FrameRingBase(int channels, int capacityFrames, int prebufferFrames, int prebufferTimeoutMs)
    {
        Channels = Math.Max(1, channels);
        _buffer = new T[Math.Max(1, capacityFrames) * Channels];
        _prebufferFrames = Math.Max(0, prebufferFrames);
        _prebufferTimeoutMs = Math.Max(0, prebufferTimeoutMs);
    }

    public int CapacityFrames => _buffer.Length / Channels;
    public long FramesPlayed => Interlocked.Read(ref _framesPlayed);
    public long UnderrunCallbacks => Interlocked.Read(ref _underrunCallbacks);
    public bool IsDrained { get { lock (_gate) { return InputEnded && _head == _tail; } } }
    public int ReadyFrames { get { lock (_gate) { return (int)(_head - _tail); } } }

    public void BeginSession()
    {
        lock (_gate)
        {
            _epoch++;
            _head = _tail = 0;
            _sessionHasAudio = false;
            InputEnded = false;
            _prebuffering = _prebufferFrames > 0;
            _prebufferDeadlineTicks = Stopwatch.GetTimestamp()
                + Math.Max(1, _prebufferTimeoutMs) * Stopwatch.Frequency / 1000;
        }
        Interlocked.Exchange(ref _framesPlayed, 0);
        Interlocked.Exchange(ref _underrunCallbacks, 0);
        Interlocked.Exchange(ref _underrunFrames, 0);
        OnSessionBegin();
    }

    /// <summary>会话开始/seek 重置钩子（DoP 重置标记相位基）。</summary>
    protected virtual void OnSessionBegin() { }

    public void MarkInputEnded() => InputEnded = true;

    /// <summary>唤醒可能在等待的生产者（seek 重置/销毁时调用）。</summary>
    public void WakeProducer() { lock (_gate) { Monitor.Pulse(_gate); } }

    /// <summary>
    /// 生产者推送 frameCount 帧（interleaved）。返回 false = 会话已重置（seek）或已取消，
    /// 调用方应回循环处理待决 seek / 退出。seek 竞态防护：阻塞在满环上的生产者被
    /// BeginSession 唤醒后，凭代数比对丢弃 seek 前的旧数据而非写入新会话。
    /// </summary>
    public bool Push(ReadOnlySpan<T> source, int frameCount, Func<bool> cancelled)
    {
        long epoch;
        lock (_gate)
        {
            epoch = _epoch;
            if (frameCount > 0) _sessionHasAudio = true;
        }

        int written = 0;
        while (written < frameCount)
        {
            lock (_gate)
            {
                if (epoch != _epoch) return false;
                int free = CapacityFrames - (int)(_head - _tail);
                if (free > 0)
                {
                    int take = Math.Min(free, frameCount - written);
                    long headLocal = _head % CapacityFrames;
                    int first = Math.Min(take, CapacityFrames - (int)headLocal);
                    CopyIn(source, written, (int)headLocal, first);
                    if (first < take)
                        CopyIn(source, written + first, 0, take - first);
                    _head += take;
                    written += take;
                    continue;
                }
                Monitor.Wait(_gate, 4);
            }
            if (cancelled()) return false;
        }
        return true;
    }

    /// <summary>
    /// 消费者渲染：先用静音填满整块（DoP 为标记静音），再搬运可用数据。
    /// 预缓冲期直接返回 0（全静音）。ECHO 语义：返回实际读取帧数。
    /// </summary>
    public int Render(Span<T> output, int frameCount)
    {
        if (frameCount <= 0 || output.Length < frameCount * Channels) return 0;
        long phaseBase = NextRenderPhase(frameCount);
        FillSilence(output, frameCount, phaseBase);
        if (HoldForPrebuffer()) return 0;

        int readTotal = 0;
        lock (_gate)
        {
            int needed = frameCount;
            int outFrame = 0;
            while (needed > 0)
            {
                int ready = (int)(_head - _tail);
                if (ready <= 0)
                {
                    if (!InputEnded && _sessionHasAudio)
                    {
                        Interlocked.Increment(ref _underrunCallbacks);
                        Interlocked.Add(ref _underrunFrames, needed);
                    }
                    break;
                }
                int take = Math.Min(ready, needed);
                long tailLocal = _tail % CapacityFrames;
                int first = Math.Min(take, CapacityFrames - (int)tailLocal);
                CopyOut((int)tailLocal, output, outFrame, first);
                if (first < take)
                    CopyOut(0, output, outFrame + first, take - first);
                _tail += take;
                readTotal += take;
                outFrame += take;
                needed -= take;
            }
        }
        if (readTotal > 0)
        {
            Interlocked.Add(ref _framesPlayed, readTotal);
            PostRender(output, frameCount, phaseBase);
        }
        return readTotal;
    }

    private bool HoldForPrebuffer()
    {
        lock (_gate)
        {
            if (!_prebuffering) return false;
            int ready = (int)(_head - _tail);
            bool enough = ready >= _prebufferFrames;
            bool timedOut = _prebufferTimeoutMs <= 0 || Stopwatch.GetTimestamp() >= _prebufferDeadlineTicks;
            if (enough || timedOut || InputEnded)
            {
                _prebuffering = false;
                return false;
            }
            return true;
        }
    }

    /// <summary>块渲染前推进并返回相位基（DoP 用全局帧计数保证标记跨块连续；仅渲染线程调用）。</summary>
    protected virtual long NextRenderPhase(int frameCount) => 0;

    /// <summary>整块静音（DoP 覆盖为标记静音；phaseBase = 本块首帧的全局帧号）。</summary>
    protected virtual void FillSilence(Span<T> output, int frameCount, long phaseBase)
        => output[..(frameCount * Channels)].Clear();

    /// <summary>渲染后处理（DoP 在此按全局帧号重盖标记位）。</summary>
    protected virtual void PostRender(Span<T> output, int frameCount, long phaseBase) { }

    private void CopyIn(ReadOnlySpan<T> source, int sourceFrame, int ringFrame, int frames)
        => source.Slice(sourceFrame * Channels, frames * Channels)
            .CopyTo(_buffer.AsSpan(ringFrame * Channels, frames * Channels));

    private void CopyOut(int ringFrame, Span<T> output, int outFrame, int frames)
        => _buffer.AsSpan(ringFrame * Channels, frames * Channels)
            .CopyTo(output.Slice(outFrame * Channels, frames * Channels));
}

/// <summary>PCM：float 交织。</summary>
internal sealed class PcmRing : FrameRingBase<float>
{
    public PcmRing(int channels, int capacityFrames, int prebufferFrames, int prebufferMs)
        : base(channels, capacityFrames, prebufferFrames, prebufferMs) { }
}

/// <summary>
/// DoP：uint32 采样（低 16 位 DSD 数据，位 16-23 标记）。标记按<b>全局</b>渲染帧计数
/// 交替（0x05 起始）：缓冲帧数为奇数时（如独占事件 6615 帧）按块内帧号交替会在每块
/// 边界翻转相位 → DAC 无法锁定 → 全程静音（KA13 实测）。静音低 16 位填 0x6969
/// （DSD 静音字节），避免锁定瞬间全 0 位流的直流跳变。
/// </summary>
internal sealed class DopRing : FrameRingBase<uint>
{
    private long _renderedFrames; // 渲染线程独占（BeginSession 重置）

    public DopRing(int channels, int capacityFrames, int prebufferFrames, int prebufferMs)
        : base(channels, capacityFrames, prebufferFrames, prebufferMs) { }

    protected override void OnSessionBegin() => _renderedFrames = 0;

    protected override long NextRenderPhase(int frameCount)
    {
        long b = _renderedFrames;
        _renderedFrames += frameCount; // 静音块同样推进：相位在任何路径下连续
        return b;
    }

    protected override void FillSilence(Span<uint> output, int frameCount, long phaseBase)
    {
        for (int f = 0; f < frameCount; f++)
        {
            uint sample = (uint)(((phaseBase + f) & 1) == 0 ? 0x05u : 0xfau) << 16 | 0x6969u;
            for (int c = 0; c < Channels; c++)
                output[f * Channels + c] = sample;
        }
    }

    protected override void PostRender(Span<uint> output, int frameCount, long phaseBase)
    {
        for (int f = 0; f < frameCount; f++)
        {
            uint marker = (uint)(((phaseBase + f) & 1) == 0 ? 0x05u : 0xfau) << 16;
            for (int c = 0; c < Channels; c++)
                output[f * Channels + c] = (output[f * Channels + c] & 0x0000ffffu) | marker;
        }
    }
}

/// <summary>原生 DSD：每帧每声道 1 字节（MSB 优先）。静音 = 0x69（ECHO 约定）。</summary>
internal sealed class DsdByteRing : FrameRingBase<byte>
{
    public DsdByteRing(int channels, int capacityByteFrames, int prebufferFrames, int prebufferMs)
        : base(channels, capacityByteFrames, prebufferFrames, prebufferMs) { }

    protected override void FillSilence(Span<byte> output, int frameCount, long phaseBase)
        => output[..(frameCount * Channels)].Fill((byte)0x69);
}
