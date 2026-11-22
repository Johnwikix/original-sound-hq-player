using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Reader;

namespace WinUIMusicPlayer.Helper;

/// <summary>MTP 不提供文件系统路径；先在本机生成完整文件，再交给 Windows 存储提供程序传输。</summary>
public sealed class UsbPortableStorageWriter : IDisposable
{
    private readonly StorageFolder _folder;
    private StorageFile? _lastCopiedFile;
    private int _disposed;

    private UsbPortableStorageWriter(StorageFolder folder) => _folder = folder;

    public static async Task<UsbPortableStorageWriter> OpenAsync(UsbStorageDevice device, string author, string album,
        CancellationToken token = default)
    {
        await UsbStorageDeviceReader.PortableIoGate.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var folder = await UsbStorageDeviceReader.OpenPortableStorageAsync(device);
            foreach (string name in new[] { "MUSIC", author, album })
            {
                try { folder = await folder.GetFolderAsync(name); }
                catch (FileNotFoundException)
                {
                    try { folder = await folder.CreateFolderAsync(name, CreationCollisionOption.OpenIfExists); }
                    catch (Exception ex) { throw new IOException($"创建便携式目录失败: {folder.Name}/{name}", ex); }
                }
            }
            return new UsbPortableStorageWriter(folder);
        }
        catch
        {
            UsbStorageDeviceReader.PortableIoGate.Release();
            throw;
        }
    }

    public async Task CopyAsync(string sourcePath, bool overwrite = true)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath);
        _lastCopiedFile = await source.CopyAsync(_folder, source.Name, overwrite ? NameCollisionOption.ReplaceExisting : NameCollisionOption.FailIfExists);
    }

    public async Task<UsbFileFingerprint> GetLastCopiedFingerprintAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var file = _lastCopiedFile ?? throw new InvalidOperationException("尚未复制文件");
        var basic = await file.GetBasicPropertiesAsync();
        long? modified = basic.DateModified > DateTimeOffset.FromFileTime(0) ? basic.DateModified.UtcDateTime.Ticks : null;
        return new(checked((long)basic.Size), modified);
    }

    /// <summary>同步已明确清空的歌词侧车，缺失文件无需处理，其他驱动错误交给发送结果报告。</summary>
    public async Task DeleteFileIfExistsAsync(string name)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        try
        {
            var file = await _folder.GetFileAsync(name);
            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }
        catch (FileNotFoundException) { return; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lastCopiedFile = null;
            UsbStorageDeviceReader.PortableIoGate.Release();
        }
    }
}
