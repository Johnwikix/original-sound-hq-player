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
    /// <summary>累计提交音频帧数；Native DSD 使用字节帧。</summary>
    long SubmittedFrames => 0;
    /// <summary>PCM：读 frames 帧交织 double（float64 管线，含 EQ 与增益），不足补静音。</summary>
    void FillPcm(Span<double> buffer, int frames);
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
    /// <summary>最后音频帧是否已通过设备输出管线。</summary>
    bool IsDrained => true;
    /// <summary>设备管线中尚未消费的音频时长，按回调周期估算。</summary>
    long PendingAudioMs => 0;

    /// <summary>端点缓冲深度（毫秒）：渲染领先可闻播放的量。淡出后需等它排空再停，否则尾段被硬裁。</summary>
    int LatencyMs { get; }

    /// <summary>渲染是否已失败（设备拔出/独占被抢占等）——引擎看门狗据此停机并通知。</summary>
    bool IsFailed { get; }

    /// <summary>输出所在端点 ID（MMDevice ID 字符串；ASIO/无法取得为 null）。用于匹配端点通知。</summary>
    string? DeviceId => null;

    /// <summary>是否跟随系统默认设备（DirectSound / 默认设备的 WASAPI 共享）：默认设备变更时应换端点。</summary>
    bool FollowsDefaultDevice => false;
}
