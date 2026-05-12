using ATL;
using CommunityToolkit.WinUI;
using DffTagReader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Xaml.Interactivity;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.WebService;
using ZLinq;

namespace WinUIMusicPlayer.Behaviors;

public class AlbumCoverBehavior : Behavior<Image>
{
    // ── 静态共享状态 ──────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ImageSource> _coverCache = new();
    private static readonly ConcurrentQueue<string> _cacheQueue = new();
    // key = imageHash 优先，无 hash 时 = "id:{musicId}"
    private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _loadingTasks = new();

    // ── 依赖属性 ──────────────────────────────────────────────────────────

    public static readonly DependencyProperty MusicProperty =
        DependencyProperty.Register(nameof(Music), typeof(Music), typeof(AlbumCoverBehavior),
            new PropertyMetadata(null, OnMusicChanged));

    public Music Music
    {
        get => (Music)GetValue(MusicProperty);
        set => SetValue(MusicProperty, value);
    }

    public static int CoverSize { get; set; } = 150; // 专辑封面大小，单位为像素

    private static void OnMusicChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is AlbumCoverBehavior b)
            b.Load(e.NewValue as Music);
    }

    public static int MaxCacheSize { get; set; } = 1000;   // 0 = 禁用缓存，< 0 = 无限制，> 0 = 最多保留 N 张

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

        // 内存缓存命中：直接显示，不淡入（滚动回来时已有图）
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
        var coverSize = CoverSize; // 捕获当前值，避免闭包竞态

        // 复用或新建加载任务
        var task = _loadingTasks.GetOrAdd(cacheKey, _ =>
        {
            var bitmap = new BitmapImage { DecodePixelWidth = coverSize };
            return LoadCoreAsync(music, cacheKey, bitmap, coverSize);
        });

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
        catch { }
    }

    // ── 加载核心（静态，供多实例共用） ────────────────────────────────────

    private static async Task<ImageSource?> LoadCoreAsync(
        Music music, string cacheKey, BitmapImage bitmap, int coverSize)
    {
        try
        {
            return await LoadImageAsync(music, bitmap, coverSize);
        }
        catch
        {
            return null;
        }
        finally
        {
            _loadingTasks.TryRemove(cacheKey, out _);
        }
    }

    // ── 图片加载 ──────────────────────────────────────────────────────────

    private static async Task<ImageSource?> LoadImageAsync(Music music, BitmapImage bitmap, int coverSize)
    {
        return await Task.Run(async () =>
        {
            try
            {
                // ── 磁盘缓存读取（方案三：直接文件流，不拷贝到内存） ──────
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
                    && !string.IsNullOrEmpty(music.ImageHash))
                {
                    var cachePath = GetDiskCachePath(music.ImageHash, coverSize);

                    if (System.IO.File.Exists(cachePath))
                    {
                        if (System.IO.File.GetLastWriteTime(cachePath) > System.IO.File.GetLastWriteTime(music.Path))
                        {
                            // 直接用 StorageFile 流，零拷贝进内存
                            var result = await LoadFromFilePathAsync(cachePath, music, bitmap);
                            if (result != null) return result;
                        }
                        else
                        {
                            try { System.IO.File.Delete(cachePath); } catch { }
                        }
                    }
                }

                // ── 从标签读取原始图片 ────────────────────────────────────
                byte[]? picture =  await ToolUtils.GetRawImage(music);
                return await DecodePictureAsync(picture, music, bitmap, coverSize);
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    // ── 解码并写缓存 ──────────────────────────────────────────────────────

    private static async Task<ImageSource?> DecodePictureAsync(
        byte[]? picture, Music music, BitmapImage bitmap, int coverSize)
    {
        if (picture is not { Length: > 0 })
        {
            return null;
        }
        return await Task.Run(async () =>
        {
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
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, outputStream);
                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync();

                // ── 方案四：流式写磁盘，不分配额外 byte[] ─────────────────
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache))
                {
                    try
                    {
                        var cachePath = GetDiskCachePath(music.ImageHash, coverSize);
                        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                        outputStream.Seek(0);
                        using var fs = new FileStream(
                            cachePath, FileMode.Create, FileAccess.Write,
                            FileShare.None, bufferSize: 81920, useAsync: true);
                        await outputStream.AsStream().CopyToAsync(fs);
                        // fs 在此 using 块结束时 Flush+Close，outputStream 不受影响
                    }
                    catch { }
                }

                outputStream.Seek(0);

                // ── 回到 UI 线程：SetSource 完成后写内存缓存并返回 ────────
                ImageSource? result = null;
                await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
                {
                    try
                    {
                        await bitmap.SetSourceAsync(outputStream);

                        var cacheKey = CacheKey(music);
                        AddToCache(cacheKey, bitmap, MaxCacheSize);


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
            catch { return null; }
            finally
            {
                softwareBitmap?.Dispose();
                outputStream?.Dispose();
            }
        });
    }

    // ── 从文件路径加载（磁盘缓存命中路径，方案三：零拷贝） ───────────────
    // 原 LoadFromBytesAsync：byte[] → InMemoryRandomAccessStream → BitmapImage（两份拷贝）
    // 现在：StorageFile → FileStream → BitmapImage（零拷贝，不分配中间 byte[]）

    private static async Task<ImageSource?> LoadFromFilePathAsync(
        string cachePath, Music music, BitmapImage bitmap)
    {
        try
        {
            // GetFileFromPathAsync 要求绝对路径，在后台线程获取 StorageFile
            var storageFile = await StorageFile.GetFileFromPathAsync(cachePath);

            ImageSource? result = null;
            await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
            {
                IRandomAccessStream? stream = null;
                try
                {
                    stream = await storageFile.OpenReadAsync();
                    await bitmap.SetSourceAsync(stream);
                    AddToCache(music.ImageHash, bitmap, MaxCacheSize);
                    result = bitmap;
                }
                finally
                {
                    stream?.Dispose();
                }
            });

            return result;
        }
        catch { return null; }
    }

    // ── 网络获取 ──────────────────────────────────────────────────────────

    //private static async Task<ImageSource?> FetchFromNetAsync(
    //    byte[]? picture, Music music, BitmapImage bitmap, int coverSize)
    //{
    //    try
    //    {
    //        if (!Directory.Exists(AppSettings.MusicCoverCache))
    //            Directory.CreateDirectory(AppSettings.MusicCoverCache);

    //        string fileName = $"{music.Title}_{music.Album}_{music.Author}";
    //        string invalidChars = new string(Path.GetInvalidFileNameChars())
    //                            + new string(Path.GetInvalidPathChars());
    //        fileName = Regex.Replace(fileName, $"[{Regex.Escape(invalidChars)}]", "_");
    //        string filePath = Path.Combine(AppSettings.MusicCoverCache, fileName + ".png");

    //        if (System.IO.File.Exists(filePath))
    //        {
    //            picture = System.IO.File.ReadAllBytes(filePath);
    //        }
    //        else if (AppSettings.IsAutoLyricsEnabled)
    //        {
    //            picture = await App.Services.GetRequiredService<LrcService>().GetMixedCoverImageAsync(music);

    //            if (picture is { Length: > 0 })
    //                System.IO.File.WriteAllBytes(filePath, picture);
    //        }

    //        if (picture is { Length: > 0 })
    //            return await DecodePictureAsync(picture, music, bitmap, coverSize);
    //    }
    //    catch { }

    //    return null;
    //}

    // ── 缓存管理 ──────────────────────────────────────────────────────────

    private static void AddToCache(string key, ImageSource source, int maxSize)
    {
        if (maxSize == 0) return;

        if (!_coverCache.TryAdd(key, source))
            return;   // key 已存在，跳过

        _cacheQueue.Enqueue(key);

        if (maxSize < 0)
            return;   // 无限制模式

        // 超限时持续弹出最旧的项，直到满足上限
        while (_coverCache.Count > maxSize && _cacheQueue.TryDequeue(out var oldestKey))
        {
            _coverCache.TryRemove(oldestKey, out _);
        }
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

    /// <summary>
    /// 缓存 key 包含 coverSize，不同尺寸的同一张封面视为不同缓存项。
    /// </summary>
    private static string CacheKey(Music music) =>
        string.IsNullOrEmpty(music.ImageHash)
            ? $"id:{music.Id}"
            : music.ImageHash;

    private static string GetDiskCachePath(string imageHash, int coverSize)
    {
        var cacheFolder = Path.Combine(AppSettings.MusicCoverCache, "Cache");
        return Path.Combine(cacheFolder, $"{imageHash}_{coverSize}.png");
    }
}