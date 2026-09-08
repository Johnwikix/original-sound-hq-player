namespace AudioPlayer.Playback;

/// <summary>渲染源种类：PCM（含 DSD→PCM）/ DoP 位流 / 原生 DSD 位流。</summary>
internal enum RenderKind
{
    Pcm,
    Dop,
    NativeDsd,
}

/// <summary>
/// 输出层从引擎拉取音频的接口。全部方法在输出渲染线程调用，
/// 实现必须无锁、无分配（内部使用预分配缓冲与原子快照）。
/// </summary>
internal interface IRenderSource
{
    RenderKind Kind { get; }
    /// <summary>设备侧速率：PCM=采样率；DoP=DoP 帧率(DSD/16)；NativeDsd=DSD 位率。</summary>
    int SampleRate { get; }
    int Channels { get; }
    /// <summary>PCM：读 frames 帧交织 float（含 EQ 与增益），不足补静音。</summary>
    void FillPcm(Span<float> buffer, int frames);
    /// <summary>DoP：读 frames 帧 uint32 采样（标记位已在环内归一化）。</summary>
    void FillDop(Span<uint> buffer, int frames);
    /// <summary>NativeDSD：读 byteFrames 个字节帧（每帧每声道 1 字节 MSB 优先）。</summary>
    void FillDsdBytes(Span<byte> buffer, int byteFrames);
}

/// <summary>输出后端公共面。</summary>
internal interface IAudioOutput : IDisposable
{
    void Pause();
    void Resume();
}
