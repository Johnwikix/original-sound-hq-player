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

    private static readonly Channel<PendingCover> _channel =
        Channel.CreateBounded<PendingCover>(new BoundedChannelOptions(64)
        {
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    private static readonly List<Thread> _workers = new();
    private static readonly Channel<PendingCover> _remoteChannel = Channel.CreateBounded<PendingCover>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private static readonly CancellationTokenSource _shutdownCts = new();
    private static readonly object _initLock = new();
    private static int _initialized;

    private sealed class DecodedCover(int width, int height, byte[] pixels)
    {
        public int Width { get; } = width;
        public int Height { get; } = height;
        public byte[] Pixels { get; } = pixels;
    }

    private static readonly ConcurrentDictionary<string, PendingCover> _pendingTasks = new();

    private sealed class PendingCover(
        Music Music,
        string CacheKey,
        int CoverSize)
    {
        private readonly CancellationTokenSource _workCts = new();
        private int _waiters;
        private int _completed;
        private int _disposed;

        public Music Music { get; } = Music;
        public string CacheKey { get; } = CacheKey;
        public int CoverSize { get; } = CoverSize;
        public TaskCompletionSource<DecodedCover?> Tcs { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token => _workCts.Token;
        public int WaiterCount => Volatile.Read(ref _waiters);

        public void AddWaiter() => Interlocked.Increment(ref _waiters);

        public void ReleaseWaiter()
        {
            if (Interlocked.Decrement(ref _waiters) == 0 && Volatile.Read(ref _completed) == 0)
            {
                try { _workCts.Cancel(); }
                catch (ObjectDisposedException) { }
            }
            DisposeIfComplete();
        }

        public void Complete()
        {
            Volatile.Write(ref _completed, 1);
            DisposeIfComplete();
        }

        private void DisposeIfComplete()
        {
            if (Volatile.Read(ref _completed) != 0 && Volatile.Read(ref _waiters) == 0
                && Interlocked.Exchange(ref _disposed, 1) == 0)
                _workCts.Dispose();
        }
    }

    public static async Task<ImageSource?> EnqueueAsync(Music music, CancellationToken token)
    {
        EnsureInitialized();
        var cacheKey = CacheKey(music);
        PendingCover pending;
        bool isNew = false;
        while (true)
        {
            if (_pendingTasks.TryGetValue(cacheKey, out var existing))
            {
                if (!existing.Tcs.Task.IsCompleted || existing.Tcs.Task.Status == TaskStatus.RanToCompletion)
                {
                    pending = existing;
                    break;
                }

                RemovePending(existing);
                continue;
            }

            // 共享的是有界的解码像素，而不是 SoftwareBitmapSource。
            // 每个消费者随后创建并拥有自己的 WinRT source，可独立 Dispose。
            var candidate = new PendingCover(music, cacheKey, CoverSize);

            if (!_pendingTasks.TryAdd(cacheKey, candidate)) continue;
            pending = candidate;
            isNew = true;
            break;
        }

        pending.AddWaiter();
        try
        {
            if (isNew)
            {
                var channel = music.IsRemote ? _remoteChannel : _channel;
                try
                {
                    await channel.Writer.WriteAsync(pending, token);
                }
                catch (OperationCanceledException)
                {
                    // If another consumer joined while this request was waiting
                    // for queue space, keep the shared request alive and finish
                    // enqueueing it without the canceled consumer's token.
                    if (pending.WaiterCount > 1)
                    {
                        try { await channel.Writer.WriteAsync(pending, CancellationToken.None); }
                        catch (ChannelClosedException)
                        {
                            RemovePending(pending);
                            pending.Tcs.TrySetCanceled();
                            pending.Complete();
                        }
                        throw;
                    }

                    RemovePending(pending);
                    pending.Tcs.TrySetCanceled(token);
                    pending.Complete();
                    throw;
                }
                catch (ChannelClosedException)
                {
                    RemovePending(pending);
                    pending.Tcs.TrySetCanceled();
                    pending.Complete();
                    return null;
                }
            }

            var decoded = await pending.Tcs.Task.WaitAsync(token);
            if (decoded is null || token.IsCancellationRequested) return null;
            return await CreateImageSourceAsync(decoded, token);
        }
        finally
        {
            pending.ReleaseWaiter();
        }
    }

    private static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) != 0) return;
        lock (_initLock)
        {
            if (_initialized != 0) return;
            int n = Math.Max(1, WorkerCount);
            for (int i = 0; i <= n; i++)
            {
                var channel = i == n ? _remoteChannel : _channel;
                var t = new Thread(() => WorkerLoop(channel))
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

    private static void WorkerLoop(Channel<PendingCover> channel)
    {
        while (!_shutdownCts.IsCancellationRequested)
        {
            try
            {
                InnerLoop(channel);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "CoverLoadQueue worker crashed, restarting in 1s");
                try { Thread.Sleep(1000); } catch { return; }
            }
        }
    }

    private static void InnerLoop(Channel<PendingCover> channel)
    {
        var ct = _shutdownCts.Token;
        while (!ct.IsCancellationRequested)
        {
            PendingCover req;
            try
            {
                req = channel.Reader.ReadAsync(ct).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { return; }
            catch (ChannelClosedException) { return; }

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
                RemovePending(req);
                req.Complete();
            }
        }
    }

    private static async Task<DecodedCover?> LoadAndDecodeAsync(PendingCover req)
    {
        req.Token.ThrowIfCancellationRequested();

        // ① 缩略图像素缓存查找
        if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
            && !string.IsNullOrEmpty(req.Music.ImageHash))
        {
            var thumbPath = GetThumbCachePath(req.Music.ImageHash, req.CoverSize);
            if (File.Exists(thumbPath))
            {
                if (req.Music.IsRemote || File.GetLastWriteTime(thumbPath) > File.GetLastWriteTime(req.Music.Path))
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
        byte[]? picture = await ToolUtils.GetRawImage(req.Music, false, req.Token);
        if (picture is not { Length: > 0 }) return null;

        req.Token.ThrowIfCancellationRequested();

        return await DecodeAndCacheThumbAsync(picture, req.Music, req.CoverSize, req.Token);
    }

    private static async Task<DecodedCover?> LoadThumbFromCacheAsync(
        string cachePath, CancellationToken token)
    {
        try
        {
            var header = ArrayPool<byte>.Shared.Rent(54);
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

                    var pixels = new byte[pixelBytes];
                    await fs.ReadExactlyAsync(pixels.AsMemory(), token);
                    return new DecodedCover(w, h, pixels);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(header, clearArray: false);
            }
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "LoadThumbFromCacheAsync 失败，删除损坏缓存: {Path}", cachePath);
            try { File.Delete(cachePath); } catch { }
            return null;
        }
    }

    private static async Task<DecodedCover?> DecodeAndCacheThumbAsync(
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

            uint w = (uint)softwareBitmap.PixelWidth;
            uint h = (uint)softwareBitmap.PixelHeight;
            int pixelBytes = checked((int)(w * h * 4));
            var pixels = new byte[pixelBytes];
            softwareBitmap.CopyToBuffer(pixels.AsBuffer());

            // 写缩略图像素缓存（Bgra8 裸像素，约 90KB）
            if (music.ImageHash is { Length: > 0 })
            {
                try
                {
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
                    await fs.WriteAsync(pixels.AsMemory(), token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { _logger?.LogError(ex, "写缩略图缓存失败"); }
            }

            token.ThrowIfCancellationRequested();
            return new DecodedCover((int)w, (int)h, pixels);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "DecodeAndCacheThumbAsync 失败");
            return null;
        }
        finally { softwareBitmap?.Dispose(); }
    }

    private static async Task<ImageSource?> CreateImageSourceAsync(DecodedCover decoded, CancellationToken token)
    {
        ImageSource? result = null;
        SoftwareBitmapSource? source = null;
        try
        {
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                token.ThrowIfCancellationRequested();
                using var softwareBitmap = new SoftwareBitmap(
                    BitmapPixelFormat.Bgra8,
                    decoded.Width,
                    decoded.Height,
                    BitmapAlphaMode.Premultiplied);
                softwareBitmap.CopyFromBuffer(decoded.Pixels.AsBuffer());
                source = new SoftwareBitmapSource();
                await source.SetBitmapAsync(softwareBitmap);
                result = source;
                source = null;
            });
            token.ThrowIfCancellationRequested();
            var completed = result;
            result = null;
            return completed;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "创建缩略图 SoftwareBitmapSource 失败");
            return null;
        }
        finally
        {
            source?.Dispose();
            (result as IDisposable)?.Dispose();
        }
    }

    public static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? $"id:{music.Id}"
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
        _remoteChannel.Writer.TryComplete();
        while (_channel.Reader.TryRead(out var request) || _remoteChannel.Reader.TryRead(out request))
        {
            RemovePending(request);
            request.Tcs.TrySetCanceled();
            request.Complete();
        }

        var t = timeout ?? TimeSpan.FromSeconds(3);
        foreach (var w in _workers)
        {
            try { w.Join(t); } catch { }
            if (w.IsAlive) _logger?.LogWarning("CoverLoadQueue worker did not exit within {Timeout}", t);
        }
        _workers.Clear();
    }

    private static void RemovePending(PendingCover request) =>
        ((ICollection<KeyValuePair<string, PendingCover>>)_pendingTasks)
            .Remove(new KeyValuePair<string, PendingCover>(request.CacheKey, request));
}
