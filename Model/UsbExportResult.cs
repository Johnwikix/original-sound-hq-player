using System.Collections.Generic;

namespace WinUIMusicPlayer.Model;

public enum UsbExportFailureStage { Audio, Lyrics, Status }

public sealed record UsbExportFailure(Music Music, UsbExportFailureStage Stage, string Message);

/// <summary>音频成功与歌词/状态失败分别记录，不能用处理进度代替传输结果。</summary>
public sealed class UsbExportResult(int requestedCount)
{
    private readonly List<(Music Music, string Extension)> _succeeded = new(requestedCount);
    private readonly List<UsbExportFailure> _failures = [];
    public int RequestedCount { get; } = requestedCount;
    public IReadOnlyList<(Music Music, string Extension)> Succeeded => _succeeded;
    public IReadOnlyList<UsbExportFailure> Failures => _failures;
    public int UnsentCount => RequestedCount - _succeeded.Count;
    public int LyricsFailureCount { get; private set; }
    public int StatusFailureCount { get; private set; }
    public bool HasFailures => UnsentCount > 0 || _failures.Count > 0;

    internal void AddSuccess(Music music, string extension) => _succeeded.Add((music, extension));

    internal void AddFailure(Music music, UsbExportFailureStage stage, string message)
    {
        _failures.Add(new(music, stage, message));
        if (stage == UsbExportFailureStage.Lyrics) LyricsFailureCount++;
        if (stage == UsbExportFailureStage.Status) StatusFailureCount++;
    }
}
