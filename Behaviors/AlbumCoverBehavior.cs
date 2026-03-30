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
using WinUIMusicPlayer.Reader;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.WebService;
using ZLinq;

namespace WinUIMusicPlayer.Behaviors;

public class AlbumCoverBehavior : Behavior<Image>
{
    // ── 静态共享状态 ──────────────────────────────────────────────────────

    private static readonly ConcurrentDictionary<string, ImageSource> _coverCache = new();

    // key = imageHash 优先，无 hash 时 = "id:{musicId}"
    private static readonly ConcurrentDictionary<string, TaskCompletionSource<ImageSource?>> _loadingTcs = new();
    private static readonly ConcurrentDictionary<string, CancellationTokenSource> _loadingCts = new();

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

    public static void OnMusicUnloaded(int musicId)
    {
        var key = $"id:{musicId}";
        if (_loadingCts.TryRemove(key, out var cts))
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }
        _loadingTcs.TryRemove(key, out _);
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
        var tcs = _loadingTcs.GetOrAdd(cacheKey, key =>
        {
            var newTcs = new TaskCompletionSource<ImageSource?>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var cts = new CancellationTokenSource();
            _loadingCts[cacheKey] = cts;
            _ = LoadCoreAsync(music, cacheKey, newTcs, cts.Token);
            return newTcs;
        });

        _ = WaitAndApplyAsync(tcs, token);
    }

    private async Task WaitAndApplyAsync(
        TaskCompletionSource<ImageSource?> tcs, CancellationToken token)
    {
        try
        {
            var source = await tcs.Task.WaitAsync(token);
            if (token.IsCancellationRequested || AssociatedObject == null || source == null) return;
            AssociatedObject.Source = source;
            FadeIn();
        }
        catch (OperationCanceledException) { }
    }

    // ── 加载核心（静态，供多实例共用） ────────────────────────────────────

    private static async Task LoadCoreAsync(
        Music music, string cacheKey,
        TaskCompletionSource<ImageSource?> tcs,
        CancellationToken ct)
    {
        try
        {
            var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
            await LoadImageAsync(music, bitmap, tcs, ct);
        }
        catch (OperationCanceledException)
        {
            tcs.TrySetCanceled();
        }
        catch
        {
            tcs.TrySetResult(null);
        }
        finally
        {
            _loadingTcs.TryRemove(cacheKey, out _);
            _loadingCts.TryRemove(cacheKey, out _);
        }
    }

    // ── 图片加载（原 ToolUtils.LoadImageAsync） ───────────────────────────

    private static async Task LoadImageAsync(
        Music music, BitmapImage bitmap,
        TaskCompletionSource<ImageSource?> tcs,
        CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            try
            {
                if (ct.IsCancellationRequested) return;

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
                            var bytes = await System.IO.File.ReadAllBytesAsync(cachePath, ct);

                            // 优先检查缓存图片尺寸是否与当前 CoverSize 一致
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
                                // 尺寸不匹配，直接删除，走原始流程重新解码并覆写缓存
                                try { System.IO.File.Delete(cachePath); } catch { }
                            }
                            else
                            {
                                var cacheInfo = new FileInfo(cachePath);
                                DateTime musicTime = music.UpdateTime != default
                                    ? music.UpdateTime
                                    : music.CreateTime;

                                // 缓存比 music 更新，直接使用
                                if (cacheInfo.LastWriteTime > musicTime)
                                {
                                    if (!ct.IsCancellationRequested)
                                    {
                                        await LoadFromBytesAndNotify(bytes, music, bitmap, tcs, ct);
                                        return;
                                    }
                                }
                                // 缓存过期：删除旧文件，继续走原始流程重新解码并覆写缓存
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
                    if (ct.IsCancellationRequested) return;
                    if (picture is null || picture.Length == 0) {
                        using var file = TagLib.File.Create(music.Path, ReadStyle.None);
                        if (ct.IsCancellationRequested) return;
                        picture = file.Tag.Pictures.AsValueEnumerable().FirstOrDefault()?.Data.Data;
                    }
                }

                if (picture is { Length: > 0 } && !ct.IsCancellationRequested)
                {
                    await DecodePicture(picture, music, bitmap, tcs, ct);
                    if (!tcs.Task.IsCompleted && !ct.IsCancellationRequested)
                        await FetchFromNet(picture, music, bitmap, tcs, ct);
                    return;
                }

                if (!ct.IsCancellationRequested)
                    await FetchFromNet(null, music, bitmap, tcs, ct);
            }
            catch (OperationCanceledException) {}
            catch (Exception)
            {
                // 降级：用 ATL 读取
                try
                {
                    var track = new Track(music.Path);
                    var picture = track?.EmbeddedPictures.AsValueEnumerable().FirstOrDefault()?.PictureData;
                    if (picture is { Length: > 0 } && !ct.IsCancellationRequested)
                    {
                        await DecodePicture(picture, music, bitmap, tcs, ct);
                        if (!tcs.Task.IsCompleted && !ct.IsCancellationRequested)
                            await FetchFromNet(picture, music, bitmap, tcs, ct);
                        return;
                    }
                    if (!ct.IsCancellationRequested)
                        await FetchFromNet(null, music, bitmap, tcs, ct);
                }
                catch (OperationCanceledException) {}
                catch
                {
                    try { await FetchFromNet(null, music, bitmap, tcs, ct); } catch { }
                }
            }
        }, ct);
    }

    // ── 解码并写缓存（原 DecodePicture） ──────────────────────────────────

    private static async Task DecodePicture(
        byte[] picture, Music music, BitmapImage bitmap,
        TaskCompletionSource<ImageSource?> tcs,
        CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            SoftwareBitmap? softwareBitmap = null;
            try
            {
                // 计算原始图片 hash
                var imageHash = Convert.ToHexString(
                    System.Security.Cryptography.MD5.HashData(picture));

                using var stream = new InMemoryRandomAccessStream();
                await stream.WriteAsync(picture.AsBuffer());
                stream.Seek(0);
                if (ct.IsCancellationRequested) return;

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

                if (ct.IsCancellationRequested) return;

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
                            await outputStream.AsStream()
                                .ReadExactlyAsync(buf, 0, buf.Length, ct);
                            await System.IO.File.WriteAllBytesAsync(cachePath, buf, ct);
                        }
                    }
                    catch { }
                }

                outputStream.Seek(0);
                if (ct.IsCancellationRequested) { outputStream.Dispose(); return; }

                // ── 回到 UI 线程：SetSource 完成后才写缓存并通知 ──────────
                await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (ct.IsCancellationRequested) { outputStream.Dispose(); return; }
                    try
                    {
                        await bitmap.SetSourceAsync(outputStream);

                        // 写内存缓存
                        _coverCache.TryAdd(imageHash, bitmap);

                        // 更新 music.ImageHash 并持久化
                        if (music.ImageHash != imageHash)
                        {
                            music.ImageHash = imageHash;
                            await App.Services
                                .GetRequiredService<MusicDatabaseService>()
                                .UpdateMusicInfo(music);
                        }

                        // SetSource 完成后才通知所有等待者
                        tcs.TrySetResult(bitmap);
                    }
                    catch (Exception ex)
                    {
                        tcs.TrySetException(ex);
                    }
                    finally
                    {
                        outputStream.Dispose();
                    }
                });
            }
            catch (OperationCanceledException) {}
            finally { softwareBitmap?.Dispose(); }
        }, ct);
    }

    // ── 从字节流加载（磁盘缓存命中路径） ─────────────────────────────────

    private static async Task LoadFromBytesAndNotify(
        byte[] bytes, Music music, BitmapImage bitmap,
        TaskCompletionSource<ImageSource?> tcs,
        CancellationToken ct)
    {
        await Task.Run(async () =>
        {
            try
            {
                var outputStream = new InMemoryRandomAccessStream();
                await outputStream.WriteAsync(bytes.AsBuffer());
                outputStream.Seek(0);
                if (ct.IsCancellationRequested) { outputStream.Dispose(); return; }

                await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
                {
                    if (ct.IsCancellationRequested) { outputStream.Dispose(); return; }
                    try
                    {
                        await bitmap.SetSourceAsync(outputStream);
                        _coverCache.TryAdd(music.ImageHash, bitmap);

                        // SetSource 完成后通知
                        tcs.TrySetResult(bitmap);
                    }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                    finally { outputStream.Dispose(); }
                });
            }
            catch (OperationCanceledException) {}
        }, ct);
    }

    // ── 网络获取（原 GetPicFromNet，签名不变） ────────────────────────────

    private static async Task FetchFromNet(
        byte[]? picture, Music music, BitmapImage bitmap,
        TaskCompletionSource<ImageSource?> tcs,
        CancellationToken ct)
    {
        try
        {
            if (picture is { Length: > 0 })
            {
                if (!tcs.Task.IsCompleted)
                    tcs.TrySetResult(null);
                return;
            }

            if (!Directory.Exists(AppSettings.MusicCoverCache))
                Directory.CreateDirectory(AppSettings.MusicCoverCache);

            if (ct.IsCancellationRequested) return;

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
                    music.Title, music.Album, music.Author, ct);

                if (picture is { Length: > 0 })
                    System.IO.File.WriteAllBytes(filePath, picture);
            }

            if (picture is { Length: > 0 } && !ct.IsCancellationRequested)
            {
                await DecodePicture(picture, music, bitmap, tcs, ct);
                return;
            }
        }
        catch (OperationCanceledException) {}
        catch { }

        if (!tcs.Task.IsCompleted)
            tcs.TrySetResult(null);
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