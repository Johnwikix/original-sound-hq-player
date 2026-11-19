using System;

namespace AnimatedWin2dControls.Controls;

/// <summary>通过不可变的 IPC 快照提供原始双声道 FFT，画布串行调用。</summary>
public interface IPlaybackSpectrumSource
{
    void SetActive(bool active);
    bool TryRead(out PlaybackSpectrumFrame frame);
}

public readonly record struct PlaybackSpectrumFrame(long Sequence, long Epoch, long ReceivedAtMs,
    int SampleRate, int FftSize, ReadOnlyMemory<float> Left, ReadOnlyMemory<float> Right)
{
    public bool IsAvailable => SampleRate > 0 && FftSize > 0 && Left.Length > 1 && Right.Length > 1;
}
