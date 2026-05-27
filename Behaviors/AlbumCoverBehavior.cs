using CommunityToolkit.WinUI;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Behaviors;

public class AlbumCoverBehavior : Behavior<Image>
{
    private static readonly ILogger<AlbumCoverBehavior> _logger =
        WinUIMusicPlayer.App.GetLogger<AlbumCoverBehavior>();

    // ── 静态共享状态 ──────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ImageSource> _coverCache = new();
    private static readonly ConcurrentQueue<string> _cacheQueue = new();
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _loadingTasks = new();

    // ── 磁盘缓存目录缓存（避免每次 Path.Combine） ─────────────────────────
    // 懒加载，AppSettings.MusicCoverCache 启动后不变
    private static string? _diskCacheFolder;
    private static string DiskCacheFolder =>
        _diskCacheFolder ??= Path.Combine(AppSettings.MusicCoverCache, "Cache");

    // ── 依赖属性 ──────────────────────────────────────────────────────────

    public static readonly DependencyProperty MusicProperty =
        DependencyProperty.Register(nameof(Music), typeof(Music), typeof(AlbumCoverBehavior),
            new PropertyMetadata(null, OnMusicChanged));

    public Music Music
    {
        get => (Music)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    public static int CoverSize { get; set; } = 150;
    public static int MaxCacheSize { get; set; } = 1000;

    private static void OnMusicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlbumCoverBehavior b)
            b.Load(e.NewValue as Music);
    }

    // ── 实例字段 ──────────────────────────────────────────────────────────

    private CancellationTokenSource? _cts;

    // ── Behavior 生命周期 ─────────────────────────────────────────────────

    protected override void OnAttached()
    {
        base.OnAttached();
        if (Music != null) Load(Music);
    }

    protected override void OnDetaching()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        base.OnDetaching();
    }

    // ── 核心加载 ──────────────────────────────────────────────────────────

    private void Load(Music? music)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;

        if (music == null || AssociatedObject == null) return;

        var cacheKey = CacheKey(music);
        if (_coverCache.TryGetValue(cacheKey, out var cached))
        {
            AssociatedObject.Opacity = 1;
            AssociatedObject.Source = cached;
            return;
        }

        AssociatedObject.Opacity = 0;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var coverSize = CoverSize;

        // ── 优化：用 TryGetValue + TryAdd 替换 GetOrAdd(key, factory)
        //    GetOrAdd 即使 key 存在也会创建 factory 闭包（捕获 music/coverSize/cacheKey）
        //    TryGetValue 命中时零分配
        if (!_loadingTasks.TryGetValue(cacheKey, out var task))
        {
            var bitmap = new BitmapImage { DecodePixelWidth = coverSize };
            var newTask = LoadCoreAsync(music, cacheKey, bitmap, coverSize);
            // 若两线程同时到达，只有一个能 TryAdd 成功；失败方复用已有 task
            task = _loadingTasks.TryAdd(cacheKey, newTask) ? newTask
                   : _loadingTasks.GetOrAdd(cacheKey, newTask); // 极小竞争窗口，此时 factory 是已有对象
        }

        _ = WaitAndApplyAsync(task, token);
    }

    private async Task WaitAndApplyAsync(Task<ImageSource?> task, CancellationToken token)
    {
        try
        {
            var source = await task.WaitAsync(token);
            if (token.IsCancellationRequested || AssociatedObject == null || source == null) return;
            AssociatedObject.Source = source;
            FadeIn();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _logger.LogError(ex, "WaitAndApplyAsync 操作失败"); }
    }

    // ── 加载核心 ──────────────────────────────────────────────────────────

    private static async Task<ImageSource?> LoadCoreAsync(
        Music music, string cacheKey, BitmapImage bitmap, int coverSize)
    {
        try
        {
            return await LoadImageAsync(music, bitmap, coverSize);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LoadCoreAsync 操作失败");
            return null;
        }
        finally
        {
            _loadingTasks.TryRemove(cacheKey, out _);
        }
    }

    // ── 图片加载（去掉双层 Task.Run，合并为单层） ─────────────────────────

    private static async Task<ImageSource?> LoadImageAsync(
        Music music, BitmapImage bitmap, int coverSize)
    {
        // 只在这里 Task.Run 一次，DecodePictureAsync 内不再嵌套 Task.Run
        return await Task.Run(async () =>
        {
            try
            {
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
                    && !string.IsNullOrEmpty(music.ImageHash))
                {
                    var cachePath = GetDiskCachePath(music.ImageHash, coverSize);
                    if (File.Exists(cachePath))
                    {
                        if (File.GetLastWriteTime(cachePath) > File.GetLastWriteTime(music.Path))
                        {
                            var result = await LoadFromFilePathAsync(cachePath, music, bitmap);
                            if (result != null) return result;
                        }
                        else
                        {
                            try { File.Delete(cachePath); }
                            catch (Exception ex) { _logger.LogError(ex, "删除旧磁盘缓存失败"); }
                        }
                    }
                }

                byte[]? picture = await ToolUtils.GetRawImage(music);
                // 直接 await，不再 Task.Run 嵌套
                return await DecodePictureAsync(picture, music, bitmap, coverSize);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "LoadImageAsync 操作失败");
                return null;
            }
        });
    }

    // ── 解码并写缓存（不再自身 Task.Run，由调用方的 Task.Run 覆盖 CPU 部分）──

    private static async Task<ImageSource?> DecodePictureAsync(
        byte[]? picture, Music music, BitmapImage bitmap, int coverSize)
    {
        if (picture is not { Length: > 0 })
            return null;

        SoftwareBitmap? softwareBitmap = null;
        InMemoryRandomAccessStream? outputStream = null;
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

            outputStream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            await encoder.FlushAsync();

            // ── 写磁盘缓存（流式，不分配额外 byte[]） ─────────────────────
            if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache))
            {
                try
                {
                    var cachePath = GetDiskCachePath(music.ImageHash, coverSize);
                    Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                    outputStream.Seek(0);
                    await using var fs = new FileStream(
                        cachePath, FileMode.Create, FileAccess.Write,
                        FileShare.None, bufferSize: 81920, useAsync: true);
                    await outputStream.AsStream().CopyToAsync(fs);
                }
                catch (Exception ex) { _logger.LogError(ex, "写磁盘缓存失败"); }
            }

            outputStream.Seek(0);

            // ── 回 UI 线程：SetSource ───────────────────────────────────
            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                try
                {
                    await bitmap.SetSourceAsync(outputStream);
                    AddToCache(CacheKey(music), bitmap, MaxCacheSize);
                    result = bitmap;
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
            _logger.LogError(ex, "DecodePictureAsync 操作失败");
            return null;
        }
        finally
        {
            softwareBitmap?.Dispose();
            outputStream?.Dispose();
        }
    }

    // ── 磁盘缓存命中路径 ──────────────────────────────────────────────────

    private static async Task<ImageSource?> LoadFromFilePathAsync(
        string cachePath, Music music, BitmapImage bitmap)
    {
        try
        {
            var storageFile = await StorageFile.GetFileFromPathAsync(cachePath);
            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                IRandomAccessStream? stream = null;
                try
                {
                    stream = await storageFile.OpenReadAsync();
                    await bitmap.SetSourceAsync(stream);
                    AddToCache(CacheKey(music), bitmap, MaxCacheSize);
                    result = bitmap;
                }
                finally
                {
                    stream?.Dispose();
                }
            });
            return result;
        }
        catch (Exception ex) { _logger.LogError(ex, "LoadFromFilePathAsync 操作失败"); return null; }
    }

    // ── 缓存管理 ──────────────────────────────────────────────────────────

    private static void AddToCache(string key, ImageSource source, int maxSize)
    {
        if (maxSize == 0) return;
        if (!_coverCache.TryAdd(key, source)) return;

        _cacheQueue.Enqueue(key);
        if (maxSize < 0) return;

        while (_coverCache.Count > maxSize && _cacheQueue.TryDequeue(out var oldestKey))
            _coverCache.TryRemove(oldestKey, out _);
    }

    // ── 淡入动画 ──────────────────────────────────────────────────────────

    private void FadeIn()
    {
        if (AssociatedObject == null) return;
        var ani = new DoubleAnimation
        {
            From = 0,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(300),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var sb = new Storyboard();
        sb.Children.Add(ani);
        Storyboard.SetTarget(ani, AssociatedObject);
        Storyboard.SetTargetProperty(ani, "Opacity");
        sb.Begin();
    }

    // ── 工具 ──────────────────────────────────────────────────────────────

    private static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? string.Intern($"id:{music.Id}")   // Id 有限，Intern 复用字符串对象
            : music.ImageHash;                   // ImageHash 本身已是 interned/复用字符串

    private static string GetDiskCachePath(string imageHash, int coverSize)
    {
        // DiskCacheFolder 已缓存，只剩一次插值
        // ── 进一步优化：用 string.Create 避免两次 Path.Combine 的中间串 ──
        var fileName = string.Create(
            imageHash.Length + 1 + GetDigitCount(coverSize) + 4, // +4 = ".png"
            (imageHash, coverSize),
            static (span, state) =>
            {
                state.imageHash.AsSpan().CopyTo(span);
                int pos = state.imageHash.Length;
                span[pos++] = '_';
                state.coverSize.TryFormat(span[pos..], out int written);
                pos += written;
                ".png".AsSpan().CopyTo(span[pos..]);
            });
        return Path.Combine(DiskCacheFolder, fileName);
    }

    // ── 辅助：计算整数位数（避免 ToString 分配） ─────────────────────────
    private static int GetDigitCount(int n)
    {
        if (n < 10) return 1;
        if (n < 100) return 2;
        if (n < 1000) return 3;
        return 4; // coverSize 不会超过 9999
    }
}