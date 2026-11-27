using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics.Imaging;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

internal static class ListeningReportImages
{
    internal const int Width = 1080;
    internal const int Height = 1350;
    internal const uint CoverSize = 256;
    private const long MaxCoverBytes = 8 * 1024 * 1024;
    private const ulong MaxSourcePixels = 4_000_000;

    internal static Task<SoftwareBitmap?> DecodeCoverAsync(string? hash, CancellationToken token)
    {
        if (string.IsNullOrEmpty(hash) || hash.AsSpan().IndexOfAnyExcept("0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ-_") >= 0)
            return Task.FromResult<SoftwareBitmap?>(null);
        return Task.Run(() => DecodeCachedCoverAsync(hash, token), token);
    }

    private static async Task<SoftwareBitmap?> DecodeCachedCoverAsync(string hash, CancellationToken token)
    {
        string path = ToolUtils.FindRawCachePath(hash);
        if (!File.Exists(path)) path = CoverLoadQueue.GetThumbCachePath(hash, CoverLoadQueue.CoverSize);
        if (!File.Exists(path)) return null;
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
            1, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > MaxCoverBytes) return null;
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask().ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        if (width == 0 || height == 0 || (ulong)width * height > MaxSourcePixels) return null;
        double scale = Math.Min(1d, (double)CoverSize / Math.Max(width, height));
        return await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform
            {
                ScaledWidth = Math.Max(1u, (uint)(width * scale)),
                ScaledHeight = Math.Max(1u, (uint)(height * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant
            }, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
    }

    internal static async Task<string?> PickPathAsync(WindowId owner, string suggestedName, CancellationToken token)
    {
        var picker = new FileSavePicker(owner)
        {
            SuggestedFileName = suggestedName,
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            DefaultFileExtension = ".png"
        };
        picker.FileTypeChoices.Add("PNG", [".png"]);
        return (await picker.PickSaveFileAsync().AsTask(token))?.Path;
    }

    internal static async Task<SoftwareBitmap> CaptureAsync(FrameworkElement poster, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (poster is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 })
            throw new InvalidOperationException("Report poster must be visible and measured.");
        // RenderTargetBitmap 的缩放参数以 DIP 为单位。限制物理像素，避免高 DPI 放大 GPU 纹理及回读缓冲。
        double scale = poster.XamlRoot.RasterizationScale;
        int widthDip = (int)Math.Floor(Width / scale);
        int heightDip = (int)Math.Floor(Height / scale);
        var target = new RenderTargetBitmap();
        await target.RenderAsync(poster, widthDip, heightDip).AsTask();
        token.ThrowIfCancellationRequested();
        int width = target.PixelWidth;
        int height = target.PixelHeight;
        if (width is <= 0 or > Width || height is <= 0 or > Height)
            throw new IOException($"Report poster capture returned {width} × {height} pixels.");
        var pixels = await target.GetPixelsAsync().AsTask();
        token.ThrowIfCancellationRequested();
        if (pixels.Length != width * height * 4)
            throw new IOException($"Report poster capture returned an invalid pixel buffer for {width} × {height} pixels.");

        // 位图在原生侧复制一次供 WIC 编码，不物化 BGRA8 大数组。
        return SoftwareBitmap.CreateCopyFromBuffer(pixels, BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }

    internal static async Task SaveAsync(string path, SoftwareBitmap bitmap, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".{Guid.NewGuid():N}.part");
        try
        {
            token.ThrowIfCancellationRequested();
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.Asynchronous))
            using (var output = file.AsRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output).AsTask().ConfigureAwait(false);
                encoder.SetSoftwareBitmap(bitmap);
                // 分数缩放可能使取整后的位图短一两个像素，编码时补足固定 PNG 画幅。
                if (bitmap.PixelWidth != Width || bitmap.PixelHeight != Height)
                {
                    encoder.BitmapTransform.ScaledWidth = Width;
                    encoder.BitmapTransform.ScaledHeight = Height;
                }
                await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            // 同目录重命名提交，原文件保持完整直到编码成功。
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
