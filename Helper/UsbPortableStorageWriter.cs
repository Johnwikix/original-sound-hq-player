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

    public async Task CopyAsync(string sourcePath)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var source = await StorageFile.GetFileFromPathAsync(sourcePath);
        await source.CopyAsync(_folder, source.Name, NameCollisionOption.ReplaceExisting);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            UsbStorageDeviceReader.PortableIoGate.Release();
    }
}
