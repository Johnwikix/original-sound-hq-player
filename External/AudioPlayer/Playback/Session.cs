using AudioPlayer.Decode;
using AudioPlayer.Interop;

namespace AudioPlayer.Playback;

/// <summary>
/// 一次播放会话：解码线程 + 环形缓冲 +（PCM 时）EQ/增益，实现 IRenderSource
/// 供输出层拉取。提交位置 = 锚点帧 + 环已读帧；引擎另扣设备待播管线。
/// 解码器归解码线程独占；seek 请求经标志位投递、解码线程执行，天然免锁。
/// </summary>
internal sealed class Session : IRenderSource, IDisposable
{
    private readonly int _positionRate; // 环帧域的每秒帧数（用于 ms 换算）

    private PcmDecoder? _pcm;
    private DsdRawReader? _dsd;
    private Thread? _thread;
    private volatile bool _cancelled;
    private long _pendingSeekMs = long.MinValue; // long.MinValue = 无请求
    private const long _pendingSeekSentinel = long.MinValue;
    private readonly object _seekGate = new();
    private readonly Func<bool> _isCancelled;
    private long _decodeEpoch;
    private int _disposed;
    private readonly object _decodeEndGate = new();

    // 解码 scratch（按渲染种类在构造时分配实配，未用种类保持空数组：这些缓冲均超
    // 85KB 直接进 LOH，按需分配避免每会话 ~0.46MB 无谓流失）
    private readonly double[] _decodeScratchF = [];
    private readonly byte[] _decodeScratchB = [];
    private readonly uint[] _dopPackBuffer = [];
    private readonly byte[] _dsdLeftover = [];
    private readonly byte[] _dsdMergeBuffer = []; // DoP 残留字节帧拼接（预分配，解码路径零分配）
    private int _dsdLeftoverBytes;

    private PcmRing? _pcmRing;
    private DopRing? _dopRing;
    private DsdByteRing? _dsdRing;

    internal readonly Equalizer Eq = new();
    internal readonly GainRamp? Gain;
    internal readonly PcmEffects? Effects;
    private bool _dspEnabled = true;
    private int _dspResetVersion, _renderDspResetVersion;

    /// <summary>发布音效设置；总开关关闭时音频块跳过整条效果链。</summary>
    internal void ConfigureDsp(BassPlayerIpc.Shared.DspSettings settings)
    {
        bool wasEnabled = Volatile.Read(ref _dspEnabled);
        if (!settings.IsEnabled) Volatile.Write(ref _dspEnabled, false);
        Effects?.Configure(settings);
        if (wasEnabled != settings.IsEnabled) Interlocked.Increment(ref _dspResetVersion);
        if (settings.IsEnabled) Volatile.Write(ref _dspEnabled, true);
    }

    public RenderKind Kind { get; }
    public int Channels { get; }
    public long TotalMs { get; }
    public long AnchorFrames; // 引擎线程写、渲染线程不读（位置在引擎侧合成）

    private readonly int _channels;

    public int SampleRate { get; } // 设备侧速率

    private Session(PlaybackEngine engine, RenderKind kind, int channels, int positionRate,
        int deviceRate, long totalMs, int gainRampRate)
    {
        _isCancelled = Cancelled;
        Kind = kind;
        _channels = Channels = channels;
        _positionRate = positionRate;
        SampleRate = deviceRate;
        TotalMs = totalMs;
        Gain = kind == RenderKind.Pcm && gainRampRate > 0 ? new GainRamp(gainRampRate) : null;
        Effects = kind == RenderKind.Pcm ? new PcmEffects(deviceRate, channels) : null;
        int ch = Math.Max(1, channels);
        if (kind == RenderKind.Pcm)
        {
            _decodeScratchF = new double[16384 * ch];
        }
        else if (kind == RenderKind.Dop)
        {
            _dsdLeftover = new byte[ch * 2]; // last byte frame plus one silence frame at EOF
            _decodeScratchB = new byte[65536 * ch];
            _dopPackBuffer = new uint[32768 * ch];
            _dsdMergeBuffer = new byte[65536 * ch + ch];
        }
        else // NativeDsd
        {
            _decodeScratchB = new byte[65536 * ch];
        }
    }

    // ─────────────── 工厂 ───────────────

    public static Session? Open(PlaybackEngine engine, string path, RenderKind kind,
        int dsdPcmFreq, int dsdGainDb, int latencyMs, int? forcedRate = null, int? forcedChannels = null,
        int? maxChannels = null)
    {
        switch (kind)
        {
            case RenderKind.Pcm:
            {
                var dec = new PcmDecoder();
                if (!dec.Open(path, dsdPcmFreq, dsdGainDb, forcedRate, forcedChannels, maxChannels)) { dec.Dispose(); return null; }
                int rate = dec.SampleRate;
                int channels = dec.Channels;
                int ringFrames = RingCapacity(rate, latencyMs);
                var s = new Session(engine, kind, channels, rate, rate, dec.TotalMs, rate)
                {
                    _pcm = dec,
                    _pcmRing = new PcmRing(channels, ringFrames, PrebufferFrames(rate), 300),
                };
                s.Effects!.SetFile(path, dsdPcmFreq, dsdGainDb);
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
        long byLatency = (long)framesPerSecond * Math.Clamp(latencyMs, 50, 2000) * 2 / 1000;
        return (int)Math.Clamp(byLatency, framesPerSecond / 2, Math.Min(int.MaxValue, (long)framesPerSecond * 2));
    }

    private static int PrebufferFrames(int framesPerSecond)
        => Math.Min(framesPerSecond * 3 / 10, 262144); // ~300ms

    private void StartThread(ThreadStart proc)
    {
        _thread = new Thread(proc) { IsBackground = true, Name = $"decode-{Kind}" };
        _thread.Start();
    }

    // ─────────────── 引擎侧控制 ───────────────

    public long SubmittedFrames => FramesPlayed;
    public long FramesPlayed => _pcmRing?.FramesPlayed ?? _dopRing?.FramesPlayed ?? _dsdRing?.FramesPlayed ?? 0;
    public int ReadyFrames => _pcmRing?.ReadyFrames ?? _dopRing?.ReadyFrames ?? _dsdRing?.ReadyFrames ?? 0;
    public bool IsDrained => _pcmRing?.IsDrained ?? _dopRing?.IsDrained ?? _dsdRing?.IsDrained ?? false;

    /// <summary>seek：立即重置环与锚点（进度条即时响应），解码线程随后转到新位置。</summary>
    public void RequestSeek(long targetMs)
    {
        lock (_seekGate)
        {
            Effects?.RequestReset();
            Interlocked.Increment(ref _dspResetVersion);
            Volatile.Write(ref AnchorFrames, MsToFrames(targetMs));
            _pcmRing?.BeginSession();
            _dopRing?.BeginSession();
            _dsdRing?.BeginSession();
            Interlocked.Exchange(ref _pendingSeekMs, targetMs);
        }
        WakeProducer();
    }

    public void WakeProducer()
    {
        _pcmRing?.WakeProducer();
        _dopRing?.WakeProducer();
        _dsdRing?.WakeProducer();
        lock (_decodeEndGate) Monitor.PulseAll(_decodeEndGate);
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

    private void HandleSeek()
    {
        lock (_seekGate)
        {
            if (TakeSeek(out long ms))
            {
                bool ok = _pcm?.SeekToMs(ms) ?? _dsd?.SeekToMs(ms) ?? false;
                if (!ok) Console.WriteLine($"[decode] seek failed: {ms}ms");
                _dsdLeftoverBytes = 0;
            }
            _decodeEpoch = _pcmRing?.Epoch ?? _dopRing?.Epoch ?? _dsdRing?.Epoch ?? 0;
        }
    }

    private void DisposeDecoder()
    {
        // 解码线程独占原生资源；控制线程等待超时后仍会在这里最终归还。
        _pcm?.Dispose(); _dsd?.Dispose();
        _pcm = null; _dsd = null;
    }

    private bool Cancelled() => _cancelled;

    private bool WaitForSeekAfterEnd()
    {
        // Keep the decoder/bitstream reader alive after EOF: the device may still be draining,
        // and RequestSeek must be able to restart decoding without replacing the session.
        lock (_decodeEndGate)
        {
            while (!_cancelled && Interlocked.Read(ref _pendingSeekMs) == _pendingSeekSentinel)
                Monitor.Wait(_decodeEndGate);
            return !_cancelled;
        }
    }

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
                    if (!_pcmRing!.MarkInputEnded(_decodeEpoch)) continue;
                    if (WaitForSeekAfterEnd()) continue;
                    break;
                }
                if (!_pcmRing!.Push(_decodeScratchF, frames, _isCancelled, _decodeEpoch))
                {
                    if (_cancelled) break;
                    continue; // 会话被 seek 重置：回循环顶处理待决 seek，从新位置继续
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[decode] {ex.Message}"); _pcmRing?.MarkInputEnded(_decodeEpoch); }
        finally { DisposeDecoder(); }
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
                        _dsdLeftover.AsSpan(_channels, _channels).Fill(0x69);
                        if (!PushDop(_dsdLeftover.AsSpan(0, _channels * 2))) continue;
                        _dsdLeftoverBytes = 0;
                    }
                    if (!_dopRing!.MarkInputEnded(_decodeEpoch)) continue;
                    if (WaitForSeekAfterEnd()) continue;
                    break;
                }

                bool pushed;
                if (_dsdLeftoverBytes > 0)
                {
                    // 拼接上一个包的残留字节帧：预分配缓冲复用，解码路径零分配
                    //（标准 DSF/DFF 块为偶数帧不走这里；块对齐为奇数的文件会逐包走到）
                    var merged = _dsdMergeBuffer.AsSpan(0, _dsdLeftoverBytes + bytes);
                    _dsdLeftover.AsSpan(0, _dsdLeftoverBytes).CopyTo(merged);
                    _decodeScratchB.AsSpan(0, bytes).CopyTo(merged[_dsdLeftoverBytes..]);
                    pushed = PushDop(merged);
                }
                else
                {
                    pushed = PushDop(_decodeScratchB.AsSpan(0, bytes));
                }

                if (!pushed && !_cancelled)
                    continue; // 会话被 seek 重置：回循环顶处理待决 seek，从新位置继续
            }
        }
        catch (Exception ex) { Console.WriteLine($"[decode] {ex.Message}"); _dopRing?.MarkInputEnded(_decodeEpoch); }
        finally { DisposeDecoder(); }
    }

    /// <summary>把交织 DSD 字节（每帧每声道 1 字节）装配为 DoP uint 采样并推送。
    /// 返回 false = 会话被 seek 重置（旧数据已丢弃，调用方应回循环处理待决 seek）。</summary>
    private bool PushDop(ReadOnlySpan<byte> interleaved)
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
                _dopPackBuffer[f * ch + c] = (uint)((interleaved[base0 + c] << 8) | interleaved[base1 + c]);
        }
        if (!_dopRing!.Push(_dopPackBuffer, dopFrames, _isCancelled, _decodeEpoch)) return false;

        if (leftoverFrames > 0)
        {
            interleaved.Slice(dopFrames * 2 * ch, ch).CopyTo(_dsdLeftover);
            _dsdLeftoverBytes = ch;
        }
        else _dsdLeftoverBytes = 0;
        return true;
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
                    if (!_dsdRing!.MarkInputEnded(_decodeEpoch)) continue;
                    if (WaitForSeekAfterEnd()) continue;
                    break;
                }
                int frames = bytes / _channels;
                if (frames > 0 && !_dsdRing!.Push(_decodeScratchB, frames, _isCancelled, _decodeEpoch))
                {
                    if (_cancelled) break;
                    continue; // 会话被 seek 重置：回循环顶处理待决 seek
                }
            }
        }
        catch (Exception ex) { Console.WriteLine($"[decode] {ex.Message}"); _dsdRing?.MarkInputEnded(_decodeEpoch); }
        finally { DisposeDecoder(); }
    }

    // ─────────────── IRenderSource（输出渲染线程调用） ───────────────

    public void FillPcm(Span<double> buffer, int frames)
    {
        if (_pcmRing == null || Gain == null) { buffer[..(frames * _channels)].Clear(); return; }
        int audible = _pcmRing.Render(buffer, frames);
        if (audible <= 0) return; // 预缓冲/欠载静音段：不推进 EQ 与增益斜坡（淡入淡出按出声时长走）
        if (Volatile.Read(ref _dspEnabled))
        {
            int resetVersion = Volatile.Read(ref _dspResetVersion);
            if (resetVersion != _renderDspResetVersion)
            {
                Eq.ResetHistory();
                Effects?.ResetRenderState();
                _renderDspResetVersion = resetVersion;
            }
            Effects?.ApplyInput(buffer, audible);
            Eq.Process(buffer, audible, _channels);
            Effects?.ApplyStereo(buffer, audible);
        }
        Gain.Apply(buffer, audible, _channels);
    }

    public void FillDop(Span<uint> buffer, int frames)
        => _dopRing?.Render(buffer, frames);

    public void FillDsdBytes(Span<byte> buffer, int byteFrames)
        => _dsdRing?.Render(buffer, byteFrames);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cancelled = true;
        WakeProducer();
        Effects?.Dispose();
        if (_thread == null) DisposeDecoder();
        else if (!_thread.Join(1000))
            Console.WriteLine("[decode] shutdown pending; decoder thread retains resource ownership");
    }
}
