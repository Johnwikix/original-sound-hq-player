using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Buffers;
using System.Buffers.Binary;
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

        if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
            && !string.IsNullOrEmpty(req.Music.ImageHash))
        {
            var cachePath = GetDiskCachePath(req.Music.ImageHash, req.CoverSize);
            if (File.Exists(cachePath))
            {
                if (File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(req.Music.Path))
                {
                    var result = await LoadBgra8FromDiskAsync(cachePath, req.Token);
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

        return await DecodePictureAsync(picture, req.Music, req.CoverSize, req.Token);
    }

    private static async Task<ImageSource?> LoadBgra8FromDiskAsync(
        string cachePath, CancellationToken token)
    {
        byte[]? pixels = null;
        try
        {
            uint w, h;

            await using (var fs = new FileStream(
                cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true))
            {
                byte[] header = new byte[8];
                await fs.ReadExactlyAsync(header, 0, 8, token);
                w = BinaryPrimitives.ReadUInt32LittleEndian(header);
                h = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
                if (w == 0 || h == 0) return null;

                uint pixelBytes = w * h * 4;
                if (pixelBytes > 32 * 1024 * 1024) return null; // 32MB sanity

                pixels = ArrayPool<byte>.Shared.Rent((int)pixelBytes);
                await fs.ReadExactlyAsync(pixels, 0, (int)pixelBytes, token);
            }

            var softwareBitmap = new SoftwareBitmap(
                BitmapPixelFormat.Bgra8, (int)w, (int)h, BitmapAlphaMode.Premultiplied);
            softwareBitmap.CopyFromBuffer(pixels.AsBuffer(0, (int)(w * h * 4)));

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
                finally
                {
                    softwareBitmap?.Dispose();
                }
            });
            return result;
        }
        catch (Exception ex) { _logger?.LogError(ex, "LoadBgra8FromDiskAsync 失败"); return null; }
        finally
        {
            if (pixels != null)
                ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static async Task<ImageSource?> DecodePictureAsync(
        byte[]? picture, Music music, int coverSize, CancellationToken token)
    {
        if (picture is not { Length: > 0 })
            return null;

        try
        {
            using var inputStream = new InMemoryRandomAccessStream();
            await inputStream.WriteAsync(picture.AsBuffer());
            inputStream.Seek(0);

            BitmapDecoder decoder;
            try
            {
                decoder = await BitmapDecoder.CreateAsync(inputStream);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[DecodePictureAsync] BitmapDecoder.CreateAsync 失败, 大小={Size}bytes, 前16字节={Hex}",
                    picture.Length, Convert.ToHexString(picture.AsSpan(0, Math.Min(16, picture.Length))));
                return null;
            }

            double aspect = (double)decoder.PixelWidth / decoder.PixelHeight;
            uint newW = (uint)coverSize;
            uint newH = (uint)Math.Max(1, (uint)(newW / aspect));

            SoftwareBitmap softwareBitmap;
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
                _logger?.LogError(ex, "[DecodePictureAsync] GetSoftwareBitmapAsync 失败, 原图={W}x{H}, PixelFormat={Fmt}, 目标={NewW}x{NewH}",
                    decoder.PixelWidth, decoder.PixelHeight, decoder.BitmapPixelFormat, newW, newH);
                return null;
            }

            // 写 .bgra8 磁盘缓存
            if (music.ImageHash is { Length: > 0 })
            {
                try
                {
                    uint w = (uint)softwareBitmap.PixelWidth;
                    uint h = (uint)softwareBitmap.PixelHeight;
                    uint pixelBytes = w * h * 4;
                    var pixelBuffer = new Windows.Storage.Streams.Buffer(pixelBytes);
                    softwareBitmap.CopyToBuffer(pixelBuffer);

                    var cachePath = GetDiskCachePath(music.ImageHash, coverSize);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    Span<byte> header = stackalloc byte[8];
                    BinaryPrimitives.WriteUInt32LittleEndian(header, w);
                    BinaryPrimitives.WriteUInt32LittleEndian(header[4..], h);

                    await using var fs = new FileStream(
                        cachePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 8192, useAsync: true);
                    fs.Write(header);
                    await pixelBuffer.AsStream().CopyToAsync(fs, token);
                }
                catch (Exception ex) { _logger?.LogError(ex, "写.bgra8磁盘缓存失败"); }
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
                    _logger?.LogError(ex, "[DecodePictureAsync] SoftwareBitmapSource.SetBitmapAsync 失败");
                }
                finally
                {
                    softwareBitmap?.Dispose();
                }
            });

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[DecodePictureAsync] 解码/写入/显示失败");
            return null;
        }
    }

    public static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? string.Intern($"id:{music.Id}")
            : music.ImageHash;

    private static string GetDiskCachePath(string imageHash, int coverSize)
    {
        var fileName = string.Create(
            imageHash.Length + 1 + GetDigitCount(coverSize) + 6,
            (imageHash, coverSize),
            static (span, state) =>
            {
                state.imageHash.AsSpan().CopyTo(span);
                int pos = state.imageHash.Length;
                span[pos++] = '_';
                state.coverSize.TryFormat(span[pos..], out int written);
                pos += written;
                ".bgra8".AsSpan().CopyTo(span[pos..]);
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
