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

        public void Abandon()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { _workCts.Cancel(); }
            finally { _workCts.Dispose(); }
        }

        private void DisposeIfComplete()
        {
            if (Volatile.Read(ref _completed) != 0 && Volatile.Read(ref _waiters) == 0
                && Interlocked.Exchange(ref _disposed, 1) == 0)
                _workCts.Dispose();
        }
    }

    /// <summary>返回由调用者独立拥有的封面源，沿用现有列表缩略图尺寸。</summary>
    public static async Task<ImageSource?> EnqueueAsync(Music music, CancellationToken token)
    {
        var decoded = await EnqueuePixelsAsync(music, token, CoverSize);
        return decoded is null || token.IsCancellationRequested ? null : await CreateImageSourceAsync(decoded, token);
    }

    /// <summary>保存自有图片源与封面左侧边缘色，不向控件暴露 URL 或共享可释放的图片源。</summary>
    public readonly record struct CoverArtwork(ImageSource? Source, Windows.UI.Color EdgeColor);

    /// <summary>复用同一加载、WIC 解码和缓存链路，为大幅轮播提供指定尺寸和边缘色。</summary>
    public static async Task<CoverArtwork> EnqueueArtworkAsync(Music music, CancellationToken token, int pixelSize)
    {
        var decoded = await EnqueuePixelsAsync(music, token, Math.Clamp(pixelSize, 64, 1024));
        if (decoded is null || token.IsCancellationRequested) return default;
        var source = await CreateImageSourceAsync(decoded, token);
        return new(source, ReadEdgeColor(decoded));
    }

    private static Windows.UI.Color ReadEdgeColor(DecodedCover cover)
    {
        long red = 0, green = 0, blue = 0;
        int count = 0;
        // 解码像素已在内存中；仅采样左边缘带，不重复打开图片或分配采样数组。
        for (int y = 0; y < cover.Height; y += Math.Max(1, cover.Height / 16))
            for (int x = 0; x < Math.Max(1, cover.Width / 8); x += Math.Max(1, cover.Width / 32))
            {
                int p = (y * cover.Width + x) * 4;
                int alpha = cover.Pixels[p + 3];
                if (alpha < 128) continue;
                blue += cover.Pixels[p] * 255 / alpha;
                green += cover.Pixels[p + 1] * 255 / alpha;
                red += cover.Pixels[p + 2] * 255 / alpha;
                count++;
            }
        return count > 0 ? Windows.UI.Color.FromArgb(255, (byte)(red / count), (byte)(green / count), (byte)(blue / count))
            : Windows.UI.Color.FromArgb(255, 32, 32, 32);
    }

    private static async Task<DecodedCover?> EnqueuePixelsAsync(Music music, CancellationToken token, int pixelSize)
    {
        EnsureInitialized();
        var cacheKey = string.Concat(CacheKey(music), ":", pixelSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            var candidate = new PendingCover(music, cacheKey, pixelSize);

            if (!_pendingTasks.TryAdd(cacheKey, candidate))
            {
                candidate.Abandon();
                continue;
            }
            pending = candidate;
            isNew = true;
            break;
        }

        pending.AddWaiter();
        bool waiterReleased = false;
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
                        // This caller no longer waits for the shared result. Drop
                        // its waiter count before the fallback write so the last
                        // real consumer can cancel both the work and the write.
                        pending.ReleaseWaiter();
                        waiterReleased = true;
                        try { await channel.Writer.WriteAsync(pending, pending.Token); }
                        catch (OperationCanceledException)
                        {
                            RemovePending(pending);
                            pending.Tcs.TrySetCanceled();
                            pending.Complete();
                        }
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
            return decoded;
        }
        finally
        {
            if (!waiterReleased)
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
                    cachePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
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
        catch (OperationCanceledException) { throw; }
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
            token.ThrowIfCancellationRequested();
            using var memStream = new MemoryStream(picture, writable: false);
            using var inputStream = memStream.AsRandomAccessStream();

            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            double scale = Math.Min(coverSize / (double)decoder.PixelWidth,
                2048.0 / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            uint newW = (uint)Math.Max(1, decoder.PixelWidth * scale);
            uint newH = (uint)Math.Max(1, decoder.PixelHeight * scale);

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

            token.ThrowIfCancellationRequested();
            // 临时文件完整写入并关闭后才发布，取消或失败不破坏现有可读缓存。
            if (music.ImageHash is { Length: > 0 })
            {
                string? temporary = null;
                try
                {
                    var thumbPath = GetThumbCachePath(music.ImageHash, coverSize);
                    Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
                    temporary = thumbPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
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

                    await using (var fs = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                        FileShare.None, bufferSize: 8192, useAsync: true))
                    {
                        fs.Write(header);
                        await fs.WriteAsync(pixels.AsMemory(), token);
                        await fs.FlushAsync(token);
                    }
                    token.ThrowIfCancellationRequested();
                    File.Move(temporary, thumbPath, overwrite: true);
                    temporary = null;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { _logger?.LogError(ex, "写缩略图缓存失败"); }
                finally
                {
                    if (temporary is not null)
                    {
                        try { File.Delete(temporary); }
                        catch (Exception ex) { _logger?.LogDebug(ex, "清理未发布的缩略图失败"); }
                    }
                }
            }

            token.ThrowIfCancellationRequested();
            return new DecodedCover((int)w, (int)h, pixels);
        }
        catch (OperationCanceledException) { throw; }
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
        catch (OperationCanceledException) { throw; }
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
