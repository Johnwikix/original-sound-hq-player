using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Helper;

/// <summary>扫描的增量策略及有界批次；不拥有设备通道，也不直接访问数据库或 UI。</summary>
internal sealed class UsbMusicScan(
    IReadOnlyDictionary<string, UsbDeviceMusic> cached,
    Func<IReadOnlyList<UsbDeviceMusic>, Task>? onBatch,
    CancellationToken token)
{
    private readonly List<UsbDeviceMusic> _batch = new(32);
    private long _lastPublished = Stopwatch.GetTimestamp();
    internal List<UsbDeviceMusic> Records { get; } = [];
    internal bool NeedsRetry { get; private set; }

    internal bool TryReuse(string path, long size, long modifiedTicks)
    {
        if (!cached.TryGetValue(path, out var previous) || previous.ScanMetadataRead != true
            || previous.ScanFileSize != size || previous.ScanModifiedTicks != modifiedTicks) return false;
        Records.Add(previous);
        return true;
    }

    internal UsbDeviceMusic CreateRecord(string path, string deviceId, long? size, long? modifiedTicks,
        string? title, string? author, string? album, bool metadataRead)
    {
        cached.TryGetValue(path, out var previous);
        bool sameFile = previous is not null
            && (!previous.ScanFileSize.HasValue || previous.ScanFileSize == size && previous.ScanModifiedTicks == modifiedTicks);
        // MTP 标签为空时沿用同一文件已确认的元数据；变更过的文件不能继承旧标签。
        return new UsbDeviceMusic
        {
            Id = previous?.Id ?? 0,
            Path = path,
            UniqueDeviceId = deviceId,
            Title = !string.IsNullOrWhiteSpace(title) ? title
                : sameFile && !string.IsNullOrWhiteSpace(previous!.Title) ? previous.Title : Path.GetFileNameWithoutExtension(path),
            Author = !string.IsNullOrWhiteSpace(author) ? author : sameFile ? previous!.Author : string.Empty,
            Album = !string.IsNullOrWhiteSpace(album) ? album : sameFile ? previous!.Album : string.Empty,
            Extension = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            ScanFileSize = size,
            ScanModifiedTicks = modifiedTicks,
            ScanMetadataRead = metadataRead
        };
    }

    internal async Task AddAsync(UsbDeviceMusic record)
    {
        token.ThrowIfCancellationRequested();
        Records.Add(record);
        _batch.Add(record);
        if (record.ScanMetadataRead != true) NeedsRetry = true;
        if (_batch.Count >= 32 || Stopwatch.GetElapsedTime(_lastPublished).TotalMilliseconds >= 500)
            await FlushAsync();
    }

    internal async Task FlushAsync()
    {
        token.ThrowIfCancellationRequested();
        if (_batch.Count == 0) return;
        // 回调等待数据库提交和 UI 发布后再复用缓冲，形成扫描背压。
        if (onBatch is not null) await onBatch(_batch);
        _batch.Clear();
        _lastPublished = Stopwatch.GetTimestamp();
    }

    internal Task FlushIfDueAsync()
        => _batch.Count > 0 && Stopwatch.GetElapsedTime(_lastPublished).TotalMilliseconds >= 500
            ? FlushAsync() : Task.CompletedTask;
}
