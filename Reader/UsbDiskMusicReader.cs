using ATL;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Reader;

internal sealed class UsbDiskMusicReader
{
    private UsbDiskMusicReader() { }
    private static readonly ILogger<UsbDiskMusicReader> Logger = App.GetLogger<UsbDiskMusicReader>();
    internal static async Task<UsbMusicScan> ReadAsync(UsbStorageDevice device,
        IReadOnlyDictionary<string, UsbDeviceMusic> cached, Func<IReadOnlyList<UsbDeviceMusic>, Task> onBatch,
        CancellationToken token)
    {
        var scan = new UsbMusicScan(cached, onBatch, token);
        DirectoryInfo? musicFolder = null;
        foreach (var folder in new DirectoryInfo(device.Path).EnumerateDirectories())
        {
            token.ThrowIfCancellationRequested();
            if (string.Equals(folder.Name, "MUSIC", StringComparison.OrdinalIgnoreCase)) { musicFolder = folder; break; }
        }
        // 根目录枚举成功且确实没有 Music 才返回空快照，不能用 Exists 吞掉设备未就绪/权限错误。
        if (musicFolder is null) return scan;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };
        // 和手机一致仅核对根 Music，避免遍历系统目录和回收站。
        foreach (var file in musicFolder.EnumerateFiles("*", options))
        {
            token.ThrowIfCancellationRequested();
            await scan.FlushIfDueAsync();
            if (!ToolUtils.IsMusicFile(file.Extension)) continue;
            long? size = null;
            long? modified = null;
            UsbDeviceMusic record;
            try
            {
                size = file.Length;
                modified = file.LastWriteTimeUtc.Ticks;
                if (scan.TryReuse(file.FullName, size.Value, modified.Value)) continue;
                // 不为每首歌启动 Task.Run，也不并行打开整批音频，限制可移动磁盘随机读取。
                var track = new Track(file.FullName);
                token.ThrowIfCancellationRequested();
                record = scan.CreateRecord(file.FullName, device.UniqueId, size, modified,
                    track.Title, track.Artist, track.Album, metadataRead: true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "补读 USB 标签失败，保留路径并等待重试: {Path}", file.FullName);
                record = scan.CreateRecord(file.FullName, device.UniqueId, size, modified, null, null, null, metadataRead: false);
            }
            await scan.AddAsync(record);
        }
        await scan.FlushAsync();
        return scan;
    }
}
