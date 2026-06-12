using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Windows.Foundation;
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

    private static string? _diskCacheFolder;
    private static string DiskCacheFolder =>
        _diskCacheFolder ??= Path.Combine(AppSettings.MusicCoverCache, "Cache");

    private readonly record struct CoverLoadRequest(
        Music Music,
        string CacheKey,
        BitmapImage Bitmap,
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
        var bitmap = new BitmapImage { DecodePixelWidth = CoverSize };
        var req = new CoverLoadRequest(music, cacheKey, bitmap, CoverSize, tcs, token);

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

        if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
            && !string.IsNullOrEmpty(req.Music.ImageHash))
        {
            var cachePath = GetDiskCachePath(req.Music.ImageHash, req.CoverSize);
            if (File.Exists(cachePath))
            {
                if (File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(req.Music.Path))
                {
                    var result = await LoadFromFilePathAsync(cachePath, req.Music, req.Bitmap, req.CoverSize, req.Token);
                    if (result != null) return result;
                }
                else
                {
                    try { File.Delete(cachePath); }
                    catch (Exception ex) { _logger?.LogError(ex, "删除旧磁盘缓存失败"); }
                }
            }
        }

        req.Token.ThrowIfCancellationRequested();

        byte[]? picture = await ToolUtils.GetRawImage(req.Music);

        req.Token.ThrowIfCancellationRequested();

        return await DecodePictureAsync(picture, req.Music, req.Bitmap, req.CoverSize, req.Token);
    }

    private static async Task<ImageSource?> LoadFromFilePathAsync(
        string cachePath, Music music, BitmapImage bitmap, int coverSize, CancellationToken token)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(cachePath);
            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (token.IsCancellationRequested) { result = null; return; }
                IRandomAccessStream? stream = null;
                try
                {
                    stream = await storageFile.OpenReadAsync();
                    await bitmap.SetSourceAsync(stream);
                    result = bitmap;
                }
                finally
                {
                    stream?.Dispose();
                }
            });
            if (result != null)
            {
                return result;
            }
            return null;
        }
        catch (Exception ex) { _logger?.LogError(ex, "LoadFromFilePathAsync 操作失败"); return null; }
    }

    private static async Task<ImageSource?> DecodePictureAsync(
        byte[]? picture, Music music, BitmapImage bitmap, int coverSize, CancellationToken token)
    {
        if (picture is not { Length: > 0 })
            return null;

        SoftwareBitmap? softwareBitmap = null;
        InMemoryRandomAccessStream? outputStream = null;
        BitmapDecoder? decoder = null;
        try
        {
            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(picture.AsBuffer());
            inputStream.Seek(0);

            try
            {
                decoder = await BitmapDecoder.CreateAsync(inputStream);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DecodePictureAsync] BitmapDecoder.CreateAsync 失败\nStackTrace: {Trace}\n图片大小={Size}bytes, 前16字节={Hex}",
                    ex.StackTrace, picture.Length, Convert.ToHexString(picture.AsSpan(0, Math.Min(16, picture.Length))));
                return null;
            }

            double aspect = (double)decoder.PixelWidth / decoder.PixelHeight;
            uint newW = (uint)coverSize;
            uint newH = (uint)Math.Max(1, (uint)(newW / aspect));

            try
            {
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
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DecodePictureAsync] GetSoftwareBitmapAsync 失败\nStackTrace: {Trace}\n原图={W}x{H}, OriginalPixelFormat={Fmt}, 目标={NewW}x{NewH}",
                    ex.StackTrace, decoder.PixelWidth, decoder.PixelHeight, decoder.BitmapPixelFormat, newW, newH);
                return null;
            }

            outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
            var qualityProp = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.85, PropertyType.Single) }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(qualityProp);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            if (music.ImageHash is { Length: > 0 } && coverSize > 0)
            {
                try
                {
                    var cachePath = GetDiskCachePath(music.ImageHash, coverSize);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    outputStream.Seek(0);
                    await using var fs = new FileStream(
                        cachePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 8192, useAsync: true);
                    await outputStream.AsStream().CopyToAsync(fs, bufferSize: 8192);
                }
                catch (Exception ex) { _logger?.LogError(ex, "写磁盘缓存失败"); }
            }

            outputStream.Seek(0);

            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                if (token.IsCancellationRequested) { result = null; return; }
                try
                {
                    await bitmap.SetSourceAsync(outputStream);
                    result = bitmap;
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "[DecodePictureAsync] SetSourceAsync 失败(UI线程)\nStackTrace: {Trace}", ex.StackTrace);
                }
                finally
                {
                    outputStream?.Dispose();
                    outputStream = null;
                }
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[DecodePictureAsync] 其他错误(非解码/非SetSource)\nStackTrace: {Trace}", ex.StackTrace);
            return null;
        }
        finally
        {
            softwareBitmap?.Dispose();
            outputStream?.Dispose();
        }
    }

    public static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? string.Intern($"id:{music.Id}")
            : music.ImageHash;

    private static string GetDiskCachePath(string imageHash, int coverSize)
    {
        var fileName = string.Create(
            imageHash.Length + 1 + GetDigitCount(coverSize) + 4,
            (imageHash, coverSize),
            static (span, state) =>
            {
                state.imageHash.AsSpan().CopyTo(span);
                int pos = state.imageHash.Length;
                span[pos++] = '_';
                state.coverSize.TryFormat(span[pos..], out int written);
                pos += written;
                ".jpg".AsSpan().CopyTo(span[pos..]);
            });
        return Path.Combine(DiskCacheFolder, fileName);
    }

    private static int GetDigitCount(int n)
    {
        if (n < 10) return 1;
        if (n < 100) return 2;
        if (n < 1000) return 3;
        return 4;
    }

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
