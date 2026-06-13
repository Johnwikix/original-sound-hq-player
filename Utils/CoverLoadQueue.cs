using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Utils;

internal static class CoverLoadQueue
{
    private sealed class _LoggerMarker { }
    private static readonly ILogger _logger = App.GetLogger<_LoggerMarker>();

    public static int WorkerCount { get; set; } = 2;

    public static int CoverSize { get; set; } = 150;

    private static readonly Channel<CoverLoadRequest> _channel =
        Channel.CreateUnbounded<CoverLoadRequest>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

    private static readonly List<Thread> _workers = new();
    private static readonly CancellationTokenSource _shutdownCts = new();
    private static readonly object _initLock = new();
    private static int _initialized;

    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _pendingTasks = new();

    private readonly record struct CoverLoadRequest(
        Music Music,
        string CacheKey,
        int CoverSize,
        TaskCompletionSource<ImageSource?> Tcs,
        CancellationToken Token);

    public static Task<ImageSource?> EnqueueAsync(Music music, CancellationToken token)
    {
        EnsureInitialized();
        var cacheKey = CacheKey(music);

        if (_pendingTasks.TryGetValue(cacheKey, out var existing))
        {
            if (!existing.IsCompleted || existing.Status == TaskStatus.RanToCompletion)
                return existing;
            ((ICollection<KeyValuePair<string, Task<ImageSource?>>>)_pendingTasks)
                .Remove(new KeyValuePair<string, Task<ImageSource?>>(cacheKey, existing));
        }

        var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var req = new CoverLoadRequest(music, cacheKey, CoverSize, tcs, token);

        if (_pendingTasks.TryAdd(cacheKey, tcs.Task))
        {
            _channel.Writer.TryWrite(req);
            return tcs.Task;
        }

        return _pendingTasks[cacheKey];
    }

    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0) return;
        lock (_initLock)
        {
            if (_initialized != 0) return;
            int n = Math.Max(1, WorkerCount);
            for (int i = 0; i < n; i++)
            {
                var t = new Thread(WorkerLoop)
                {
                    Name = $"AlbumCoverLoader#{i}",
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
                };
                _workers.Add(t);
                t.Start();
            }
            Volatile.Write(ref _initialized, 1);
        }
    }

    private static void WorkerLoop()
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                InnerLoop();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CoverLoadQueue worker crashed, restarting in 1s");
                try { Thread.Sleep(1000); } catch { return; }
            }
        }
    }

    private static void InnerLoop()
    {
        var ct = _shutdownCts.Token;
        while (!ct.IsCancellationRequested)
        {
            CoverLoadRequest req;
            try
            {
                req = _channel.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return; }
            catch (ChannelClosedException) { return; }

            if (req.Token.IsCancellationRequested)
            {
                _pendingTasks.TryRemove(req.CacheKey, out _);
                req.Tcs.TrySetCanceled();
                continue;
            }

            try
            {
                var src = LoadAndDecodeAsync(req).GetAwaiter().GetResult();
                req.Tcs.TrySetResult(src);
            }
            catch (OperationCanceledException) { req.Tcs.TrySetCanceled(); }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "LoadAndDecodeAsync failed for {Key}", req.CacheKey);
                req.Tcs.TrySetException(ex);
            }
            finally
            {
                _pendingTasks.TryRemove(req.CacheKey, out _);
            }
        }
    }

    private static async Task<ImageSource?> LoadAndDecodeAsync(CoverLoadRequest req)
    {
        req.Token.ThrowIfCancellationRequested();

        // ① 缩略图像素缓存查找
        if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
            && !string.IsNullOrEmpty(req.Music.ImageHash))
        {
            var thumbPath = GetThumbCachePath(req.Music.ImageHash, req.CoverSize);
            if (File.Exists(thumbPath))
            {
                if (File.GetLastWriteTime(thumbPath) > File.GetLastWriteTime(req.Music.Path))
                {
                    var result = await LoadThumbFromCacheAsync(thumbPath, req.Token);
                    if (result != null) return result;
                }
                else
                {
                    try { File.Delete(thumbPath); }
                    catch (Exception ex) { _logger?.LogError(ex, "删除旧缩略图缓存失败"); }
                }
            }
        }

        req.Token.ThrowIfCancellationRequested();

        // ② 缓存未命中：获取原始图片 → WIC 解码到缩略图尺寸 → 缓存 → 显示
        byte[]? picture = await ToolUtils.GetRawImage(req.Music);
        if (picture is not { Length: > 0 }) return null;

        req.Token.ThrowIfCancellationRequested();

        return await DecodeAndCacheThumbAsync(picture, req.Music, req.CoverSize, req.Token);
    }

    private static async Task<ImageSource?> LoadThumbFromCacheAsync(
        string cachePath, CancellationToken token)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(cachePath);
            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (token.IsCancellationRequested) return;
                IRandomAccessStream? stream = null;
                try
                {
                    stream = await storageFile.OpenReadAsync();
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(stream);
                    result = bitmap;
                }
                finally
                {
                    stream?.Dispose();
                }
            });
            return result;
        }
        catch (Exception ex) { _logger?.LogError(ex, "LoadThumbFromCacheAsync 失败"); return null; }
    }

    private static async Task<ImageSource?> DecodeAndCacheThumbAsync(
        byte[] picture, Music music, int coverSize, CancellationToken token)
    {
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(picture.AsBuffer());
            inputStream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            double aspect = (double)decoder.PixelWidth / decoder.PixelHeight;
            uint newW = (uint)coverSize;
            uint newH = (uint)Math.Max(1, (uint)(newW / aspect));

            softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                new BitmapTransform
                {
                    ScaledWidth = newW,
                    ScaledHeight = newH,
                    InterpolationMode = BitmapInterpolationMode.Fant
                },
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);

            // 写缩略图像素缓存（8B 头 + Bgra8 裸像素，90KB）
            if (music.ImageHash is { Length: > 0 })
            {
                try
                {
                    uint w = (uint)softwareBitmap.PixelWidth;
                    uint h = (uint)softwareBitmap.PixelHeight;
                    uint pixelBytes = w * h * 4;
                    var pixelBuffer = new Windows.Storage.Streams.Buffer(pixelBytes);
                    softwareBitmap.CopyToBuffer(pixelBuffer);

                    var thumbPath = GetThumbCachePath(music.ImageHash, coverSize);
                    Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);

                    Span<byte> header = stackalloc byte[54];
                    header[0] = (byte)'B'; header[1] = (byte)'M';
                    BinaryPrimitives.WriteUInt32LittleEndian(header[2..], 14 + 40 + pixelBytes);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[6..], 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[10..], 54);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 40);
                    BinaryPrimitives.WriteInt32LittleEndian(header[18..], (int)w);
                    BinaryPrimitives.WriteInt32LittleEndian(header[22..], -(int)h);
                    BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 1);
                    BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 32);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[30..], 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[34..], pixelBytes);
                    BinaryPrimitives.WriteInt32LittleEndian(header[38..], 0);
                    BinaryPrimitives.WriteInt32LittleEndian(header[42..], 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[46..], 0);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[50..], 0);

                    await using var fs = new FileStream(
                        thumbPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 8192, useAsync: true);
                    fs.Write(header);
                    await pixelBuffer.AsStream().CopyToAsync(fs, token);
                }
                catch (Exception ex) { _logger?.LogError(ex, "写缩略图缓存失败"); }
            }

            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (token.IsCancellationRequested) return;
                try
                {
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);
                    result = source;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "SoftwareBitmapSource.SetBitmapAsync 失败");
                }
                finally
                {
                    softwareBitmap?.Dispose();
                    softwareBitmap = null;
                }
            });
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DecodeAndCacheThumbAsync 失败");
            softwareBitmap?.Dispose();
            return null;
        }
    }

    public static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? string.Intern($"id:{music.Id}")
            : music.ImageHash;

    private static string GetThumbCachePath(string imageHash, int coverSize)
        => Path.Combine(AppSettings.MusicCoverCache, "Cache", $"{imageHash}_{coverSize}.bmp");

    public static void ClearImagesInContainer(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Image image)
            {
                foreach (var behavior in Interaction.GetBehaviors(image))
                {
                    if (behavior is AlbumCoverBehavior acb)
                        acb.CancelLoad();
                }
                image.Source = null;
                image.Opacity = 0;
            }
            ClearImagesInContainer(child);
        }
    }

    public static void Shutdown(TimeSpan? timeout = null)
    {
        if (Interlocked.Exchange(ref _initialized, 0) == 0) return;
        _shutdownCts.Cancel();
        _channel.Writer.TryComplete();

        var t = timeout ?? TimeSpan.FromSeconds(3);
        foreach (var w in _workers)
        {
            try { w.Join(t); } catch { }
            if (w.IsAlive) _logger?.LogWarning("CoverLoadQueue worker did not exit within {Timeout}", t);
        }
        _workers.Clear();
    }
}
