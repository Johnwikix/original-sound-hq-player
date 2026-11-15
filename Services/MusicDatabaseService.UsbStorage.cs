using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Helper;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
    public async Task ScanPortableUsbDeviceAsync(UsbStorageDevice device, CancellationToken token)
    {
        await ScanUsbDeviceAsync(device, token);
    }

    /// <summary>批次先提交再发布；仅完整目录枚举成功后处理删除。返回 false 表示仍有标签需补读。</summary>
    public async Task<bool> ScanUsbDeviceAsync(UsbStorageDevice device, CancellationToken token,
        Func<IReadOnlyList<UsbDeviceMusic>, Task>? onBatch = null, Func<IReadOnlyList<string>, Task>? onRemoved = null)
    {
        if (device.IsPortable) await UsbStorageDeviceReader.PortableIoGate.WaitAsync(token);
        try
        {
            var cached = new Dictionary<string, UsbDeviceMusic>(StringComparer.OrdinalIgnoreCase);
            var original = await GetUsbDeviceMusics(device.UniqueId);
            foreach (var record in original)
            {
                string key = UsbExportPaths.GetStoragePathKey(record.Path);
                if (!cached.TryGetValue(key, out var previous) || record.Id > previous.Id) cached[key] = record;
            }
            token.ThrowIfCancellationRequested();
            async Task CommitBatchAsync(IReadOnlyList<UsbDeviceMusic> records)
            {
                token.ThrowIfCancellationRequested();
                // 已在 MTP 通道内，不能重入 SaveUsbExportRecordsAsync 再等待同一通道。
                await _dbConnection.RunInTransactionAsync(connection =>
                {
                    token.ThrowIfCancellationRequested();
                    foreach (var record in records)
                    {
                        string key = UsbExportPaths.GetStoragePathKey(record.Path);
                        if (cached.TryGetValue(key, out var previous))
                        {
                            record.Id = previous.Id;
                            connection.Update(record);
                        }
                        else connection.Insert(record);
                        // 同一批次内的同路径也按覆盖处理；失败回滚后会丢弃本次扫描索引并重新加载。
                        cached[key] = record;
                    }
                    token.ThrowIfCancellationRequested();
                });
                if (onBatch is not null) await onBatch(records);
            }
            var scan = device.IsPortable
                ? await UsbPortableMusicReader.ReadCoreAsync(device, token, cached, CommitBatchAsync)
                : await UsbDiskMusicReader.ReadAsync(device, cached, CommitBatchAsync, token);
            token.ThrowIfCancellationRequested();
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in scan.Records) present.Add(UsbExportPaths.GetStoragePathKey(record.Path));
            var removed = new List<string>();
            foreach (var key in cached.Keys)
                if (UsbExportPaths.IsMusicPathKey(key) && !present.Contains(key)) removed.Add(key);
            await _dbConnection.RunInTransactionAsync(connection =>
            {
                token.ThrowIfCancellationRequested();
                // 完整扫描后才清理缺失路径及旧盘符留下的重复行，范围外历史记录保持不变。
                foreach (var previous in original)
                {
                    string key = UsbExportPaths.GetStoragePathKey(previous.Path);
                    if (UsbExportPaths.IsMusicPathKey(key) && (!present.Contains(key) || cached[key].Id != previous.Id))
                        connection.Delete<UsbDeviceMusic>(previous.Id);
                }
                token.ThrowIfCancellationRequested();
            });
            if (removed.Count > 0 && onRemoved is not null) await onRemoved(removed);
            return !scan.NeedsRetry;
        }
        finally
        {
            if (device.IsPortable) UsbStorageDeviceReader.PortableIoGate.Release();
        }
    }

    public async Task<IReadOnlyList<UsbDeviceMusic>> SaveUsbExportRecordsAsync(
        IReadOnlyList<(Music Music, string Extension)> sent, UsbStorageDevice device)
    {
        var records = new List<UsbDeviceMusic>(sent.Count);
        foreach (var (music, extension) in sent)
            records.Add(CreateUsbExportRecord(music, extension, device));
        return await SaveUsbExportRecordsAsync(records, device);
    }

    public static UsbDeviceMusic CreateUsbExportRecord(Music music, string extension, UsbStorageDevice device,
        UsbFileFingerprint fingerprint = default)
    {
        string relativePath = UsbExportPaths.GetRelativeFilePath(music, extension);
        return new UsbDeviceMusic
        {
            Path = device.IsPortable ? relativePath : Path.Combine(device.Path, relativePath),
            Title = music.Title,
            Author = music.Author,
            Album = music.Album,
            Extension = extension.ToUpperInvariant(),
            UniqueDeviceId = device.UniqueId,
            ScanMetadataRead = true,
            ExportSourceIdentity = UsbExportPaths.GetSourceIdentity(music),
            ScanFileSize = fingerprint.Size,
            ScanModifiedTicks = fingerprint.ModifiedTicks
        };
    }

    public async Task<IReadOnlyList<UsbDeviceMusic>> SaveUsbExportRecordsAsync(
        IReadOnlyList<UsbDeviceMusic> records, UsbStorageDevice device)
    {
        if (records.Count == 0) return records;

        if (device.IsPortable) await UsbStorageDeviceReader.PortableIoGate.WaitAsync();
        try
        {
            // 已实际写入的文件即使在退出期间也应提交台账；取消只阻止后续文件。
            await _dbConnection.RunInTransactionAsync(connection =>
            {
                var existing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var previous in connection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == device.UniqueId).ToList())
                {
                    string key = UsbExportPaths.GetStoragePathKey(previous.Path);
                    if (!existing.TryGetValue(key, out int id) || previous.Id > id) existing[key] = previous.Id;
                }
                foreach (var record in records)
                {
                    string key = UsbExportPaths.GetStoragePathKey(record.Path);
                    if (existing.TryGetValue(key, out int id))
                    {
                        record.Id = id;
                        connection.Update(record);
                    }
                    else
                    {
                        connection.Insert(record);
                        existing[key] = record.Id;
                    }
                }
            });
            return records;
        }
        finally
        {
            if (device.IsPortable) UsbStorageDeviceReader.PortableIoGate.Release();
        }
    }
}
