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
        await UsbStorageDeviceReader.PortableIoGate.WaitAsync(token);
        try
        {
            // 原生扫描与提交共用同一门，防止旧快照删除刚成功发送的记录。
            var snapshot = await UsbPortableMusicReader.ReadCoreAsync(device, token);
            token.ThrowIfCancellationRequested();
            await _dbConnection.RunInTransactionAsync(connection =>
            {
                token.ThrowIfCancellationRequested();
                var remaining = new Dictionary<string, UsbDeviceMusic>(StringComparer.OrdinalIgnoreCase);
                foreach (var record in snapshot) remaining[record.Path] = record;
                foreach (var previous in connection.Table<UsbDeviceMusic>().Where(m => m.UniqueDeviceId == device.UniqueId).ToList())
                {
                    // MTP 常仅提供文件名、空歌手/专辑。已确认的导出元数据以本地台账为准。
                    if (!remaining.Remove(previous.Path)) connection.Delete(previous);
                }
                connection.InsertAll(remaining.Values);
            });
        }
        finally
        {
            UsbStorageDeviceReader.PortableIoGate.Release();
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

    public static UsbDeviceMusic CreateUsbExportRecord(Music music, string extension, UsbStorageDevice device)
    {
        string relativePath = UsbExportPaths.GetRelativeFilePath(music, extension);
        return new UsbDeviceMusic
        {
            Path = device.IsPortable ? relativePath : Path.Combine(device.Path, relativePath),
            Title = music.Title,
            Author = music.Author,
            Album = music.Album,
            Extension = extension.ToUpperInvariant(),
            UniqueDeviceId = device.UniqueId
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
                    existing[previous.Path] = previous.Id;
                foreach (var record in records)
                {
                    if (existing.TryGetValue(record.Path, out int id))
                    {
                        record.Id = id;
                        connection.Update(record);
                    }
                    else
                    {
                        connection.Insert(record);
                        existing[record.Path] = record.Id;
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
