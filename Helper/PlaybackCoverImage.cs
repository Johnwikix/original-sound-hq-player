using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace WinUIMusicPlayer.Helper;

/// <summary>详情页与 SMTC 共用的封面文件。像素由 WIC 持有，不复制到托管大数组。</summary>
internal static class PlaybackCoverImage
{
    internal const uint MaxPixelSize = 1536;
    internal const string CacheSuffix = "_display1536_v1.png";
    // 限制冷缓存下的原生解码峰值，并合并两个消费者对同一文件的首次生成。
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);

    internal static string GetCachePath(string rawPath)
        => Path.Combine(Path.GetDirectoryName(rawPath)!,
            Path.GetFileNameWithoutExtension(rawPath) + CacheSuffix);

    internal static async Task<string> GetOrCreateAsync(string rawPath, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string cachePath = GetCachePath(rawPath);
        if (IsCurrent(cachePath, rawPath)) return cachePath;

        await DecodeGate.WaitAsync(token).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            if (IsCurrent(cachePath, rawPath)) return cachePath;
            using var file = new FileStream(rawPath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 1, FileOptions.Asynchronous);
            using var input = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(input).AsTask().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            uint longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            if (longest <= MaxPixelSize) return rawPath;

            double scale = (double)MaxPixelSize / longest;
            using var bitmap = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform
                {
                    ScaledWidth = Math.Max(1, (uint)(decoder.PixelWidth * scale)),
                    ScaledHeight = Math.Max(1, (uint)(decoder.PixelHeight * scale)),
                    InterpolationMode = BitmapInterpolationMode.Fant
                }, ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb).AsTask().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            temporary = cachePath + "." + Guid.NewGuid().ToString("N") + ".part";
            using (var outputFile = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1, FileOptions.Asynchronous))
            using (var output = outputFile.AsRandomAccessStream())
            {
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, output)
                    .AsTask().ConfigureAwait(false);
                encoder.SetSoftwareBitmap(bitmap);
                await encoder.FlushAsync().AsTask().ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, cachePath, true);
            return cachePath;
        }
        finally
        {
            if (temporary is not null)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            DecodeGate.Release();
        }
    }

    internal static bool IsCurrent(string cachePath, string rawPath)
    {
        try
        {
            var cache = new FileInfo(cachePath);
            return cache.Exists && cache.Length > 0 && File.Exists(rawPath) &&
                cache.LastWriteTimeUtc >= File.GetLastWriteTimeUtc(rawPath);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
