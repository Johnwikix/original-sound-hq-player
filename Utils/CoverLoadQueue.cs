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

    public static int WorkerCount { get; set; } = 4;

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
        // 用 None 取消 token 避免如下问题：
        // 同一 ImageHash 的多个消费者共享同一个 tcs.Task，
        // 第一个消费者的 token 若被 CancelLoad 取消，会连锁取消共享 tcs → 其他消费者丢图
        var req = new CoverLoadRequest(music, cacheKey, CoverSize, tcs, CancellationToken.None);

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

            // 注意: req.Token 固定为 CancellationToken.None (EnqueueAsync 不传入消费者 token)
            // 因此此分支永不成立，已移除。若未来需要取消支持，应改为独立机制。

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
                    try
                    {
                        var dir = Path.GetDirectoryName(thumbPath)!;
                        foreach (var file in Directory.GetFiles(dir, $"{req.Music.ImageHash}_*"))
                        {
                            try { File.Delete(file); }
                            catch (Exception ex) { _logger?.LogError(ex, "清理旧缓存失败: {File}", file); }
                        }
                    }
                    catch (Exception ex) { _logger?.LogError(ex, "清理旧缓存失败"); }
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
            var header = ArrayPool<byte>.Shared.Rent(54);
            byte[]? pixelRented = null;
            int w, h;
            int pixelBytes;

            try
            {
                await using (var fs = new FileStream(
                    cachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true))
                {
                    if (fs.Length < 58)
                    {
                        _logger?.LogWarning("缩略图缓存文件过小，删除: {Path}", cachePath);
                        fs.Close();
                        try { File.Delete(cachePath); } catch { }
                        return null;
                    }

                    await fs.ReadExactlyAsync(header.AsMemory(0, 54), token);

                    var h0 = header.AsSpan(0, 54);
                    if (h0[0] != (byte)'B' || h0[1] != (byte)'M')
                    {
                        _logger?.LogWarning("缩略图缓存 BMP magic 无效，删除: {Path}", cachePath);
                        fs.Close();
                        try { File.Delete(cachePath); } catch { }
                        return null;
                    }

                    w = BinaryPrimitives.ReadInt32LittleEndian(h0[18..]);
                    h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(h0[22..]));
                    if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
                    {
                        _logger?.LogWarning("缩略图缓存尺寸异常 ({W}x{H})，删除: {Path}", w, h, cachePath);
                        fs.Close();
                        try { File.Delete(cachePath); } catch { }
                        return null;
                    }

                    pixelBytes = w * h * 4;
                    if (fs.Length < 54 + pixelBytes)
                    {
                        _logger?.LogWarning("缩略图缓存像素数据不完整，删除: {Path}", cachePath);
                        fs.Close();
                        try { File.Delete(cachePath); } catch { }
                        return null;
                    }

                    pixelRented = ArrayPool<byte>.Shared.Rent(pixelBytes);
                    await fs.ReadExactlyAsync(pixelRented.AsMemory(0, pixelBytes), token);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header, clearArray: false);
            }

            ImageSource? result = null;
            try
            {
                await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (token.IsCancellationRequested) return;
                    using var softwareBitmap = new SoftwareBitmap(
                        BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
                    softwareBitmap.CopyFromBuffer(pixelRented.AsBuffer(0, pixelBytes));
                    var source = new SoftwareBitmapSource();
                    await source.SetBitmapAsync(softwareBitmap);
                    result = source;
                });
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pixelRented, clearArray: false);
            }
            return result;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LoadThumbFromCacheAsync 失败，删除损坏缓存: {Path}", cachePath);
            try { File.Delete(cachePath); } catch { }
            return null;
        }
    }

    private static async Task<ImageSource?> DecodeAndCacheThumbAsync(
        byte[] picture, Music music, int coverSize, CancellationToken token)
    {
        SoftwareBitmap? softwareBitmap = null;
        try
        {
            using var memStream = new MemoryStream(picture, writable: false);
            using var inputStream = memStream.AsRandomAccessStream();

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
                    int pixelBytes = (int)(w * h * 4);
                    var pixelRented = ArrayPool<byte>.Shared.Rent(pixelBytes);
                    try
                    {
                        softwareBitmap.CopyToBuffer(pixelRented.AsBuffer(0, pixelBytes));

                        var thumbPath = GetThumbCachePath(music.ImageHash, coverSize);
                        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);

                        Span<byte> header = stackalloc byte[54];
                        header[0] = (byte)'B'; header[1] = (byte)'M';
                        BinaryPrimitives.WriteUInt32LittleEndian(header[2..], (uint)(14 + 40 + pixelBytes));
                        BinaryPrimitives.WriteUInt32LittleEndian(header[6..], 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[10..], 54);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[14..], 40);
                        BinaryPrimitives.WriteInt32LittleEndian(header[18..], (int)w);
                        BinaryPrimitives.WriteInt32LittleEndian(header[22..], -(int)h);
                        BinaryPrimitives.WriteUInt16LittleEndian(header[26..], 1);
                        BinaryPrimitives.WriteUInt16LittleEndian(header[28..], 32);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[30..], 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[34..], (uint)pixelBytes);
                        BinaryPrimitives.WriteInt32LittleEndian(header[38..], 0);
                        BinaryPrimitives.WriteInt32LittleEndian(header[42..], 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[46..], 0);
                        BinaryPrimitives.WriteUInt32LittleEndian(header[50..], 0);

                        await using var fs = new FileStream(
                            thumbPath, FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: 8192, useAsync: true);
                        fs.Write(header);
                        await fs.WriteAsync(pixelRented.AsMemory(0, pixelBytes));
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(pixelRented, clearArray: false);
                    }
                }
                catch (OperationCanceledException) { }
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

    internal static string GetThumbCachePath(string imageHash, int coverSize)
        => Path.Combine(AppSettings.MusicCoverCache, "Cache", $"{imageHash}_{coverSize}.bmp");

    // 已废弃：原逻辑在 ListView 容器回收时干扰 x:Bind 重连，
    // 导致 AlbumCoverBehavior.OnMusicChanged 不被触发而丢图。
    // 所有调用方已移除。
    public static void ClearImagesInContainer(DependencyObject parent) { }

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
