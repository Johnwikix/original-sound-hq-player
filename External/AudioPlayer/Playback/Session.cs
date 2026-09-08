using AudioPlayer.Decode;
using AudioPlayer.Interop;

namespace AudioPlayer.Playback;

/// <summary>
/// 一次播放会话：解码线程 + 环形缓冲 +（PCM 时）EQ/增益，实现 IRenderSource
/// 供输出层拉取。位置 = 锚点帧 + 环已播帧（采样精确）。
/// 解码器归解码线程独占；seek 请求经标志位投递、解码线程执行，天然免锁。
/// </summary>
internal sealed class Session : IRenderSource, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly int _positionRate; // 环帧域的每秒帧数（用于 ms 换算）

    private PcmDecoder? _pcm;
    private DsdRawReader? _dsd;
    private Thread? _thread;
    private volatile bool _cancelled;
    private long _pendingSeekMs = long.MinValue; // long.MinValue = 无请求
    private readonly long _pendingSeekSentinel = long.MinValue;

    private readonly float[] _decodeScratchF;
    private readonly byte[] _decodeScratchB;
    private readonly uint[] _dopScratch;
    private readonly byte[] _dsdLeftover = new byte[64];
    private int _dsdLeftoverBytes;

    // DoP 装配后的 uint 采样复用缓冲
    private readonly uint[] _dopPackBuffer;

    private PcmRing? _pcmRing;
    private DopRing? _dopRing;
    private DsdByteRing? _dsdRing;

    internal readonly Equalizer Eq = new();
    internal readonly GainRamp? Gain;

    public RenderKind Kind { get; }
    public int Channels { get; }
    public long TotalMs { get; }
    public long AnchorFrames; // 引擎线程写、渲染线程不读（位置在引擎侧合成）
    public bool GainActive => Gain != null;

    private readonly int _channels;

    public int SampleRate { get; } // 设备侧速率

    private Session(PlaybackEngine engine, RenderKind kind, int channels, int positionRate,
        int deviceRate, long totalMs, int gainRampRate)
    {
        _engine = engine;
        Kind = kind;
        _channels = Channels = channels;
        _positionRate = positionRate;
        SampleRate = deviceRate;
        TotalMs = totalMs;
        Gain = kind == RenderKind.Pcm && gainRampRate > 0 ? new GainRamp(gainRampRate) : null;
        _decodeScratchF = new float[16384 * Math.Max(1, channels)];
        _decodeScratchB = new byte[65536 * Math.Max(1, channels)];
        _dopScratch = new uint[8192 * Math.Max(1, channels)];
        _dopPackBuffer = new uint[32768 * Math.Max(1, channels)];
    }

    // ─────────────── 工厂 ───────────────

    public static Session? Open(PlaybackEngine engine, string path, RenderKind kind,
        int dsdPcmFreq, int dsdGainDb, int latencyMs, int? forcedRate = null, int? forcedChannels = null)
    {
        switch (kind)
        {
            case RenderKind.Pcm:
            {
                var dec = new PcmDecoder();
                if (!dec.Open(path, dsdPcmFreq, dsdGainDb, forcedRate, forcedChannels)) { dec.Dispose(); return null; }
                int rate = dec.SampleRate;
                int channels = dec.Channels;
                int ringFrames = RingCapacity(rate, latencyMs);
                var s = new Session(engine, kind, channels, rate, rate, dec.TotalMs, rate)
                {
                    _pcm = dec,
                    _pcmRing = new PcmRing(channels, ringFrames, PrebufferFrames(rate), 300),
                };
                s.StartThread(s.PcmDecodeProc);
                return s;
            }
            case RenderKind.Dop:
            {
                var reader = new DsdRawReader();
                if (!reader.Open(path)) { reader.Dispose(); return null; }
                int dopRate = reader.DopSampleRate;
                int channels = reader.Channels;
                int ringFrames = RingCapacity(dopRate, latencyMs);
                var s = new Session(engine, kind, channels, dopRate, dopRate, reader.TotalMs, 0)
                {
                    _dsd = reader,
                    _dopRing = new DopRing(channels, ringFrames, PrebufferFrames(dopRate), 300),
                };
                s.StartThread(s.DopDecodeProc);
                return s;
            }
            default:
            {
                var reader = new DsdRawReader();
                if (!reader.Open(path)) { reader.Dispose(); return null; }
                int byteRate = reader.ByteRatePerChannel;
                int channels = reader.Channels;
                int ringFrames = RingCapacity(byteRate, latencyMs);
                var s = new Session(engine, kind, channels, byteRate, reader.DsdBitRate, reader.TotalMs, 0)
                {
                    _dsd = reader,
                    _dsdRing = new DsdByteRing(channels, ringFrames, PrebufferFrames(byteRate), 300),
                };
                s.StartThread(s.DsdDecodeProc);
                return s;
            }
        }
    }

    private static int RingCapacity(int framesPerSecond, int latencyMs)
    {
        // 环 ≥ 2× 设备缓冲，且夹在 [0.5s, 2s] 之间（ECHO 量级）
        int byLatency = framesPerSecond * Math.Max(50, latencyMs) * 2 / 1000;
        return Math.Clamp(byLatency, framesPerSecond / 2, framesPerSecond * 2);
    }

    private static int PrebufferFrames(int framesPerSecond)
        => Math.Min(framesPerSecond * 3 / 10, 262144); // ~300ms

    private void StartThread(ThreadStart proc)
    {
        _thread = new Thread(proc) { IsBackground = true, Name = $"decode-{Kind}" };
        _thread.Start();
    }

    // ─────────────── 引擎侧控制 ───────────────

    public long FramesPlayed => _pcmRing?.FramesPlayed ?? _dopRing?.FramesPlayed ?? _dsdRing?.FramesPlayed ?? 0;
    public int ReadyFrames => _pcmRing?.ReadyFrames ?? _dopRing?.ReadyFrames ?? _dsdRing?.ReadyFrames ?? 0;
    public bool IsDrained => _pcmRing?.IsDrained ?? _dopRing?.IsDrained ?? _dsdRing?.IsDrained ?? false;

    /// <summary>seek：立即重置环与锚点（进度条即时响应），解码线程随后转到新位置。</summary>
    public void RequestSeek(long targetMs)
    {
        AnchorFrames = MsToFrames(targetMs);
        _pcmRing?.BeginSession();
        _dopRing?.BeginSession();
        _dsdRing?.BeginSession();
        Interlocked.Exchange(ref _pendingSeekMs, targetMs);
        WakeProducer();
    }

    public void WakeProducer()
    {
        _pcmRing?.WakeProducer();
        _dopRing?.WakeProducer();
        _dsdRing?.WakeProducer();
    }

    public long CurrentMs => FramesToMs(Volatile.Read(ref AnchorFrames) + FramesPlayed);

    public long MsToFrames(long ms) => (long)Math.Round(ms * _positionRate / 1000.0);

    public long FramesToMs(long frames)
        => _positionRate > 0 ? (long)Math.Round(frames * 1000.0 / _positionRate) : 0;

    // ─────────────── 解码线程 ───────────────

    private bool TakeSeek(out long ms)
    {
        long v = Interlocked.Exchange(ref _pendingSeekMs, _pendingSeekSentinel);
        if (v == _pendingSeekSentinel) { ms = 0; return false; }
        ms = v;
        return true;
    }

    private bool HandleSeek()
    {
        if (!TakeSeek(out long ms)) return false;
        _pcm?.SeekToMs(ms);
        _dsd?.SeekToMs(ms);
        _dsdLeftoverBytes = 0;
        return true;
    }

    private bool Cancelled() => _cancelled;

    private void PcmDecodeProc()
    {
        try
        {
            while (!_cancelled)
            {
                HandleSeek();
                int frames = _pcm!.Read(_decodeScratchF);
                if (frames <= 0)
                {
                    _pcmRing!.MarkInputEnded();
                    break;
                }
                if (!_pcmRing!.Push(_decodeScratchF, frames, Cancelled)) break;
            }
        }
        catch { _pcmRing?.MarkInputEnded(); }
    }

    private void DopDecodeProc()
    {
        try
        {
            while (!_cancelled)
        {
                HandleSeek();
                int bytes = _dsd!.ReadInterleaved(_decodeScratchB);
                if (bytes <= 0)
                {
                    // 收尾：残留奇数字节帧补一个静音字节凑对
                    if (_dsdLeftoverBytes > 0 && _dopRing != null)
                    {
                        PushDop(_dsdLeftover.AsSpan(0, _channels));
                        _dsdLeftoverBytes = 0;
                    }
                    _dopRing!.MarkInputEnded();
                    break;
                }
                if (_dsdLeftoverBytes > 0)
                {
                    // 拼接上一个包的残留字节帧（解码线程冷路径，允许分配）
                    var merged = new byte[_dsdLeftoverBytes + bytes];
                    _dsdLeftover.AsSpan(0, _dsdLeftoverBytes).CopyTo(merged);
                    _decodeScratchB.AsSpan(0, bytes).CopyTo(merged.AsSpan(_dsdLeftoverBytes));
                    PushDop(merged);
                }
                else
                {
                    PushDop(_decodeScratchB.AsSpan(0, bytes));
                }
            }
        }
        catch { _dopRing?.MarkInputEnded(); }
    }

    /// <summary>把交织 DSD 字节（每帧每声道 1 字节）装配为 DoP uint 采样并推送。</summary>
    private void PushDop(ReadOnlySpan<byte> interleaved)
    {
        int ch = _channels;
        int byteFrames = interleaved.Length / ch;
        int wholeFrames = byteFrames / 2; // 2 字节帧 = 1 DoP 帧
        int leftoverFrames = byteFrames % 2;

        int dopFrames = wholeFrames;
        if (_dopPackBuffer.Length < dopFrames * ch) dopFrames = _dopPackBuffer.Length / ch;

        for (int f = 0; f < dopFrames; f++)
        {
            int base0 = (f * 2) * ch;
            int base1 = (f * 2 + 1) * ch;
            for (int c = 0; c < ch; c++)
                _dopPackBuffer[f * ch + c] = (uint)(interleaved[base0 + c] | (interleaved[base1 + c] << 8));
        }
        if (!_dopRing!.Push(_dopPackBuffer, dopFrames, Cancelled)) return;

        if (leftoverFrames > 0)
        {
            interleaved.Slice(dopFrames * 2 * ch, ch).CopyTo(_dsdLeftover);
            _dsdLeftoverBytes = ch;
        }
        else _dsdLeftoverBytes = 0;
    }

    private void DsdDecodeProc()
    {
        try
        {
            while (!_cancelled)
            {
                HandleSeek();
                int bytes = _dsd!.ReadInterleaved(_decodeScratchB);
                if (bytes <= 0)
                {
                    _dsdRing!.MarkInputEnded();
                    break;
                }
                int frames = bytes / _channels;
                if (frames > 0 && !_dsdRing!.Push(_decodeScratchB, frames, Cancelled)) break;
            }
        }
        catch { _dsdRing?.MarkInputEnded(); }
    }

    // ─────────────── IRenderSource（输出渲染线程调用） ───────────────

    public void FillPcm(Span<float> buffer, int frames)
    {
        if (_pcmRing == null || Gain == null) { buffer[..(frames * _channels)].Clear(); return; }
        int audible = _pcmRing.Render(buffer, frames);
        if (audible <= 0) return; // 预缓冲/欠载静音段：不推进 EQ 与增益斜坡（淡入淡出按出声时长走）
        Eq.Process(buffer, audible, _channels);
        Gain.Apply(buffer, audible, _channels);
    }

    public void FillDop(Span<uint> buffer, int frames)
        => _dopRing?.Render(buffer, frames);

    public void FillDsdBytes(Span<byte> buffer, int byteFrames)
        => _dsdRing?.Render(buffer, byteFrames);

    public void Dispose()
    {
        _cancelled = true;
        WakeProducer();
        try { _thread?.Join(1000); } catch { }
        if (_thread is { IsAlive: true })
        {
            // 解码线程卡在 FFmpeg 内部 IO：放弃解码器交给进程退出回收，避免析构竞争
            if (_pcm != null) { var d = _pcm; _pcm = null; _ = d; }
            if (_dsd != null) { var d = _dsd; _dsd = null; _ = d; }
        }
        _pcm?.Dispose();
        _dsd?.Dispose();
    }
}
