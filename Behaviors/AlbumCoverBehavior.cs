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
using TagLib;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;
using ZLinq;

namespace WinUIMusicPlayer.Behaviors;

public class AlbumCoverBehavior : Behavior<Image>
{
    // ── 静态共享状态 ──────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ImageSource> _coverCache = new();

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

        // 复用或新建加载任务
        var task = _loadingTasks.GetOrAdd(cacheKey, _ =>
        {
            var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
            return LoadCoreAsync(music, cacheKey, bitmap);
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
        Music music, string cacheKey, BitmapImage bitmap)
    {
        try
        {
            return await LoadImageAsync(music, bitmap);
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

    private static async Task<ImageSource?> LoadImageAsync(Music music, BitmapImage bitmap)
    {
        return await Task.Run(async () =>
        {
            try
            {
                // ── 磁盘缓存读取 ──────────────────────────────────────────
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache)
                    && !string.IsNullOrEmpty(music.ImageHash))
                {
                    var cacheFolder = Path.Combine(AppSettings.MusicCoverCache, "Cache");
                    var cachePath = Path.Combine(cacheFolder, music.ImageHash + ".png");

                    if (System.IO.File.Exists(cachePath))
                    {
                        try
                        {
                            var bytes = await System.IO.File.ReadAllBytesAsync(cachePath);

                            bool sizeMatch = false;
                            try
                            {
                                using var ms = new System.IO.MemoryStream(bytes);
                                using var img = System.Drawing.Image.FromStream(ms);
                                sizeMatch = img.Width == AppSettings.CoverSize;
                            }
                            catch { /* 图片损坏，视为不匹配 */ }

                            if (!sizeMatch)
                            {
                                try { System.IO.File.Delete(cachePath); } catch { }
                            }
                            else
                            {
                                var cacheInfo = new FileInfo(cachePath);
                                DateTime musicTime = music.UpdateTime != default
                                    ? music.UpdateTime
                                    : music.CreateTime;

                                if (cacheInfo.LastWriteTime > musicTime)
                                {
                                    return await LoadFromBytesAsync(bytes, music, bitmap);
                                }
                                else
                                {
                                    try { System.IO.File.Delete(cachePath); } catch { }
                                }
                            }
                        }
                        catch { /* 缓存损坏，继续原始流程 */ }
                    }
                }

                // ── 从标签读取原始图片 ────────────────────────────────────
                byte[]? picture = null;

                if (music.Extension.Equals("dff", StringComparison.CurrentCultureIgnoreCase))
                {
                    var res = DffId3v2Parser.ReadId3v2TagsFromDff(music.Path);
                    picture = res?.Pictures?.AsValueEnumerable().Count() > 0
                        ? res.Pictures[0]?.ImageData : null;
                }
                else
                {
                    picture = AudioCoverReader.ReadCover(music.Path);
                    if (picture is null || picture.Length == 0)
                    {
                        using var file = TagLib.File.Create(music.Path, ReadStyle.None);
                        picture = file.Tag.Pictures.AsValueEnumerable().FirstOrDefault()?.Data.Data;
                    }
                }

                if (picture is { Length: > 0 })
                {
                    var result = await DecodePictureAsync(picture, music, bitmap);
                    if (result != null) return result;
                    return await FetchFromNetAsync(picture, music, bitmap);
                }

                return await FetchFromNetAsync(null, music, bitmap);
            }
            catch (Exception)
            {
                // 降级：用 ATL 读取
                try
                {
                    var track = new Track(music.Path);
                    var picture = track?.EmbeddedPictures.AsValueEnumerable().FirstOrDefault()?.PictureData;
                    if (picture is { Length: > 0 })
                    {
                        var result = await DecodePictureAsync(picture, music, bitmap);
                        if (result != null) return result;
                        return await FetchFromNetAsync(picture, music, bitmap);
                    }
                    return await FetchFromNetAsync(null, music, bitmap);
                }
                catch
                {
                    try { return await FetchFromNetAsync(null, music, bitmap); } catch { }
                    return null;
                }
            }
        });
    }

    // ── 解码并写缓存 ──────────────────────────────────────────────────────

    private static async Task<ImageSource?> DecodePictureAsync(
        byte[] picture, Music music, BitmapImage bitmap)
    {
        return await Task.Run(async () =>
        {
            SoftwareBitmap? softwareBitmap = null;
            try
            {
                var imageHash = Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(picture));

                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(picture.AsBuffer());
                stream.Seek(0);

                var decoder = await BitmapDecoder.CreateAsync(stream);
                double aspect = (double)decoder.PixelWidth / decoder.PixelHeight;
                uint newW = (uint)AppSettings.CoverSize;
                uint newH = (uint)(newW / aspect);

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

                var outputStream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.PngEncoderId, outputStream);
                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync();

                // ── 写磁盘缓存 ────────────────────────────────────────────
                if (!string.IsNullOrEmpty(AppSettings.MusicCoverCache))
                {
                    try
                    {
                        var cacheFolder = Path.Combine(AppSettings.MusicCoverCache, "Cache");
                        Directory.CreateDirectory(cacheFolder);
                        var cachePath = Path.Combine(cacheFolder, imageHash + ".png");
                        if (!System.IO.File.Exists(cachePath))
                        {
                            outputStream.Seek(0);
                            var buf = new byte[outputStream.Size];
                            await outputStream.AsStream().ReadExactlyAsync(buf, 0, buf.Length);
                            await System.IO.File.WriteAllBytesAsync(cachePath, buf);
                        }
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

                        _coverCache.TryAdd(imageHash, bitmap);

                        if (music.ImageHash != imageHash)
                        {
                            music.ImageHash = imageHash;
                            await App.Services
                                .GetRequiredService<MusicDatabaseService>()
                                .UpdateMusicInfo(music);
                        }

                        result = bitmap;
                    }
                    finally
                    {
                        outputStream.Dispose();
                    }
                });

                return result;
            }
            catch { return null; }
            finally { softwareBitmap?.Dispose(); }
        });
    }

    // ── 从字节流加载（磁盘缓存命中路径） ─────────────────────────────────

    private static async Task<ImageSource?> LoadFromBytesAsync(
        byte[] bytes, Music music, BitmapImage bitmap)
    {
        return await Task.Run(async () =>
        {
            try
            {
                var outputStream = new InMemoryRandomAccessStream();
                await outputStream.WriteAsync(bytes.AsBuffer());
                outputStream.Seek(0);

                ImageSource? result = null;
                await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
                {
                    try
                    {
                        await bitmap.SetSourceAsync(outputStream);
                        _coverCache.TryAdd(music.ImageHash, bitmap);
                        result = bitmap;
                    }
                    finally { outputStream.Dispose(); }
                });

                return result;
            }
            catch { return null; }
        });
    }

    // ── 网络获取 ──────────────────────────────────────────────────────────

    private static async Task<ImageSource?> FetchFromNetAsync(
        byte[]? picture, Music music, BitmapImage bitmap)
    {
        try
        {
            if (picture is { Length: > 0 })
                return null;

            if (!Directory.Exists(AppSettings.MusicCoverCache))
                Directory.CreateDirectory(AppSettings.MusicCoverCache);

            string fileName = $"{music.Title}_{music.Album}_{music.Author}";
            string invalidChars = new string(Path.GetInvalidFileNameChars())
                                + new string(Path.GetInvalidPathChars());
            fileName = Regex.Replace(fileName, $"[{Regex.Escape(invalidChars)}]", "_");
            string filePath = Path.Combine(AppSettings.MusicCoverCache, fileName + ".png");

            if (System.IO.File.Exists(filePath))
            {
                picture = System.IO.File.ReadAllBytes(filePath);
            }
            else if (!AppData.UnknownAlbums.Contains(music.Album)
                     && AppSettings.IsAutoLyricsEnabled)
            {
                picture = await LrcService.GetCoverImageAsync(
                    music.Title, music.Album, music.Author);

                if (picture is { Length: > 0 })
                    System.IO.File.WriteAllBytes(filePath, picture);
            }

            if (picture is { Length: > 0 })
                return await DecodePictureAsync(picture, music, bitmap);
        }
        catch { }

        return null;
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
            ? $"id:{music.Id}"
            : music.ImageHash;
}