namespace AudioPlayer.Playback;

/// <summary>按设备消费周期追踪最后音频帧的排空；静音回调继续推进，不依赖墙钟猜测。</summary>
internal sealed class OutputDrainTracker
{
    private int _pendingFrames, _rendering;
    internal int PendingFrames => Volatile.Read(ref _pendingFrames);
    internal bool IsDrained => Volatile.Read(ref _rendering) == 0 && Volatile.Read(ref _pendingFrames) == 0;
    internal void BeginBlock() => Volatile.Write(ref _rendering, 1);
    internal void CompleteBlock(int frames, long audioFrames, int pipelineFrames)
    {
        int pending = audioFrames > 0
            ? Math.Max(0, pipelineFrames - frames) + (int)Math.Min(frames, audioFrames)
            : Math.Max(0, _pendingFrames - frames);
        Volatile.Write(ref _pendingFrames, pending);
        Volatile.Write(ref _rendering, 0);
    }
}
