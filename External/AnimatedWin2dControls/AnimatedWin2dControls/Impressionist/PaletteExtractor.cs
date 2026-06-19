using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;

namespace AnimatedWin2dControls.Impressionist;

public enum PaletteAlgorithm : byte
{
    KMeansPP,
    OctTree,
}

public static class PaletteExtractor
{
    public static async Task<PaletteResult?> ExtractFromBmpCacheAsync(
        string bmpCachePath,
        PaletteAlgorithm algorithm = PaletteAlgorithm.KMeansPP,
        CancellationToken ct = default)
    {
        var colorDict = await ImagePixelSampler.SampleFromBmpCacheAsync(bmpCachePath, 10, ct);
        if (colorDict is null || colorDict.Count == 0) return null;
        return RunAlgorithm(colorDict, 4, algorithm);
    }

    public static async Task<PaletteResult?> ExtractFromImageBytesAsync(
        byte[] imageBytes,
        PaletteAlgorithm algorithm = PaletteAlgorithm.KMeansPP,
        CancellationToken ct = default)
    {
        if (imageBytes is not { Length: > 0 }) return null;

        using var memStream = new MemoryStream(imageBytes, writable: false);
        using var rasStream = memStream.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(rasStream).AsTask(ct);

        const uint MaxSize = 150;
        uint srcW = decoder.PixelWidth, srcH = decoder.PixelHeight;
        float scale = Math.Min(1f, Math.Min((float)MaxSize / srcW, (float)MaxSize / srcH));
        uint dstW = Math.Max(1, (uint)(srcW * scale));
        uint dstH = Math.Max(1, (uint)(srcH * scale));

        ct.ThrowIfCancellationRequested();

        var pd = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Straight,
            new BitmapTransform
            {
                ScaledWidth = dstW,
                ScaledHeight = dstH,
                InterpolationMode = BitmapInterpolationMode.Fant,
            },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(ct);

        var pixels = pd.DetachPixelData();
        ct.ThrowIfCancellationRequested();

        var colorDict = ImagePixelSampler.SampleBgra8Pixels(pixels, (int)dstW, (int)dstH, 10);
        if (colorDict.Count == 0) return null;

        return RunAlgorithm(colorDict, 4, algorithm);
    }

    public static PaletteResult ExtractFromBgra8Pixels(
        ReadOnlySpan<byte> bgra8Pixels, int width, int height,
        PaletteAlgorithm algorithm = PaletteAlgorithm.KMeansPP)
    {
        var colorDict = ImagePixelSampler.SampleBgra8Pixels(bgra8Pixels, width, height, 10);
        return RunAlgorithm(colorDict, 4, algorithm);
    }

    private static PaletteResult RunAlgorithm(
        Dictionary<Vector3, int> colorDict, int clusterCount, PaletteAlgorithm algorithm)
    {
        return algorithm switch
        {
            PaletteAlgorithm.OctTree => OctTreePaletteGenerator.CreatePalette(
                colorDict, clusterCount, ignoreWhite: true),
            _ => KMeansPaletteGenerator.CreatePalette(
                colorDict, clusterCount, ignoreWhite: true, toLab: true, useKMeansPP: true),
        };
    }
}
