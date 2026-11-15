using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Search;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Reader;

public sealed class UsbPortableMusicReader
{
    private UsbPortableMusicReader() { }
    private static readonly ILogger<UsbPortableMusicReader> Logger = App.GetLogger<UsbPortableMusicReader>();

    /// <summary>只扫描 MUSIC；独立读取也遵守同一 MTP 通道。</summary>
    public static async Task<List<UsbDeviceMusic>> ReadAsync(UsbStorageDevice device, CancellationToken token)
    {
        await UsbStorageDeviceReader.PortableIoGate.WaitAsync(token);
        try { return (await ReadCoreAsync(device, token)).Records; }
        finally { UsbStorageDeviceReader.PortableIoGate.Release(); }
    }

    /// <summary>调用方持有 PortableIoGate，直到完整路径快照及其数据库提交结束。</summary>
    internal static async Task<UsbMusicScan> ReadCoreAsync(UsbStorageDevice device, CancellationToken token,
        IReadOnlyDictionary<string, UsbDeviceMusic>? cached = null, Func<IReadOnlyList<UsbDeviceMusic>, Task>? onBatch = null)
    {
        var scan = new UsbMusicScan(cached ?? new Dictionary<string, UsbDeviceMusic>(StringComparer.OrdinalIgnoreCase), onBatch, token);
        token.ThrowIfCancellationRequested();
        var root = await UsbStorageDeviceReader.OpenPortableStorageAsync(device);
        StorageFolder? musicFolder = null;
        // 有的提供程序区分大小写；枚举根目录确认 Music/MUSIC，而非把首次查找失败当作空盘。
        foreach (var folder in await root.GetFoldersAsync())
            if (string.Equals(folder.Name, "MUSIC", StringComparison.OrdinalIgnoreCase)) { musicFolder = folder; break; }
        token.ThrowIfCancellationRequested();
        if (musicFolder is null) return scan;

        var options = new QueryOptions { FolderDepth = FolderDepth.Shallow, IndexerOption = IndexerOption.DoNotUseIndexer };
        options.SetPropertyPrefetch(PropertyPrefetchOptions.BasicProperties, Array.Empty<string>());
        var pending = new Stack<(StorageFolder Folder, string Path)>();
        pending.Push((musicFolder, "MUSIC"));
        while (pending.TryPop(out var current))
        {
            token.ThrowIfCancellationRequested();
            // 只预取大小/时间；预取 MusicProperties 会让已知文件也重复读取标签。
            var query = CreateQuery(current.Folder, options);
            for (uint offset = 0; ; offset += 128)
            {
                token.ThrowIfCancellationRequested();
                IReadOnlyList<StorageFile> files;
                try
                {
                    files = query is null
                        ? await current.Folder.GetFilesAsync(CommonFileQuery.DefaultQuery, offset, 128)
                        : await query.GetFilesAsync(offset, 128);
                }
                catch (Exception ex) when (query is not null && IsQueryUnsupported(ex))
                {
                    query = null;
                    files = await current.Folder.GetFilesAsync(CommonFileQuery.DefaultQuery, offset, 128);
                }
                foreach (var file in files)
                {
                    token.ThrowIfCancellationRequested();
                    await scan.FlushIfDueAsync();
                    if (!ToolUtils.IsMusicFile(file.FileType)) continue;
                    string path = Path.Combine(current.Path, file.Name);
                    long? size = null;
                    long? modified = null;
                    UsbDeviceMusic record;
                    try
                    {
                        var basic = await file.GetBasicPropertiesAsync();
                        size = checked((long)basic.Size);
                        modified = basic.DateModified > DateTimeOffset.FromFileTime(0) ? basic.DateModified.UtcDateTime.Ticks : null;
                        if (modified.HasValue && scan.TryReuse(path, size.Value, modified.Value)) continue;
                        var properties = await file.Properties.GetMusicPropertiesAsync();
                        token.ThrowIfCancellationRequested();
                        // 提供程序有时把文件名冒充标题且不给歌手/专辑，不能据此覆盖旧导出的真实标题。
                        string? title = properties.Title;
                        if (string.IsNullOrWhiteSpace(properties.Artist) && string.IsNullOrWhiteSpace(properties.Album)
                            && string.Equals(title, Path.GetFileNameWithoutExtension(file.Name), StringComparison.Ordinal)) title = null;
                        record = scan.CreateRecord(path, device.UniqueId, size, modified,
                            title, properties.Artist, properties.Album, metadataRead: true);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        if (IsDeviceUnavailable(ex)) throw;
                        // 标签失败仍保留已枚举到的路径，不能让一首坏标签使整批缓存丢失。
                        Logger.LogWarning(ex, "补读 MTP 标签失败，保留路径并等待重试: {Path}", path);
                        record = scan.CreateRecord(path, device.UniqueId, size, modified, null, null, null, metadataRead: false);
                    }
                    await scan.AddAsync(record);
                }
                if (files.Count < 128) break;
            }
            token.ThrowIfCancellationRequested();
            for (uint offset = 0; ; offset += 128)
            {
                token.ThrowIfCancellationRequested();
                var folders = await current.Folder.GetFoldersAsync(CommonFolderQuery.DefaultQuery, offset, 128);
                foreach (var folder in folders) pending.Push((folder, Path.Combine(current.Path, folder.Name)));
                if (folders.Count < 128) break;
            }
        }
        await scan.FlushAsync();
        return scan;
    }

    private static StorageFileQueryResult? CreateQuery(StorageFolder folder, QueryOptions options)
    {
        try { return folder.AreQueryOptionsSupported(options) ? folder.CreateFileQueryWithOptions(options) : null; }
        catch (Exception ex) when (IsQueryUnsupported(ex)) { return null; }
    }

    private static bool IsQueryUnsupported(Exception error)
        => error is NotSupportedException || error.HResult is unchecked((int)0x80004001) or unchecked((int)0x80070057);

    private static bool IsDeviceUnavailable(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is DirectoryNotFoundException || current.HResult is unchecked((int)0x802A0006)
                or unchecked((int)0x8007048F) or unchecked((int)0x800706BA) or unchecked((int)0x80070015)) return true;
        return false;
    }
}
