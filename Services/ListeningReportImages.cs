using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI;
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
