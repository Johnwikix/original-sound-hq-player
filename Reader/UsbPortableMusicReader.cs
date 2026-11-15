using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Search;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Reader;

public static class UsbPortableMusicReader
{
    /// <summary>只扫描导出目录 MUSIC；不遍历手机相册、应用目录，也不把 MTP 虚拟地址交给 System.IO。</summary>
    public static async Task<List<UsbDeviceMusic>> ReadAsync(UsbStorageDevice device, CancellationToken token)
    {
        await UsbStorageDeviceReader.PortableIoGate.WaitAsync(token);
        try { return await ReadCoreAsync(device, token); }
        finally { UsbStorageDeviceReader.PortableIoGate.Release(); }
    }

    /// <summary>调用方持有 PortableIoGate，直到文件快照及其数据库提交均完成。</summary>
    internal static async Task<List<UsbDeviceMusic>> ReadCoreAsync(UsbStorageDevice device, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = await UsbStorageDeviceReader.OpenPortableStorageAsync(device);
        token.ThrowIfCancellationRequested();
        StorageFolder musicFolder;
        try { musicFolder = await root.GetFolderAsync("MUSIC"); }
        catch (FileNotFoundException) { return []; }
        var result = new List<UsbDeviceMusic>();
        var pending = new Stack<(StorageFolder Folder, string Path)>();
        pending.Push((musicFolder, "MUSIC"));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            for (uint offset = 0; ; offset += 128)
            {
                var files = await current.Folder.GetFilesAsync(CommonFileQuery.DefaultQuery, offset, 128);
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ToolUtils.IsMusicFile(file.FileType)) continue;
                    var properties = await file.Properties.GetMusicPropertiesAsync();
                    result.Add(new UsbDeviceMusic
                    {
                        Path = Path.Combine(current.Path, file.Name),
                        Title = string.IsNullOrWhiteSpace(properties.Title) ? Path.GetFileNameWithoutExtension(file.Name) : properties.Title,
                        Author = properties.Artist,
                        Album = properties.Album,
                        Extension = file.FileType.TrimStart('.').ToUpperInvariant(),
                        UniqueDeviceId = device.UniqueId
                    });
                }
                if (files.Count < 128) break;
            }
            token.ThrowIfCancellationRequested();
            foreach (var folder in await current.Folder.GetFoldersAsync())
                pending.Push((folder, Path.Combine(current.Path, folder.Name)));
        }
        return result;
    }
}
