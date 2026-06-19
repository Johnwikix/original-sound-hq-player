using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Impressionist;

internal static class ImagePixelSampler
{
    private const int BmpHeaderSize = 54;

    public static Dictionary<Vector3, int> SampleBgra8Pixels(
        ReadOnlySpan<byte> bgra8Pixels,
        int width, int height,
        int stride = 10)
    {
        int pixelCount = width * height;
        int estimatedUnique = Math.Max(16, pixelCount / stride / 4);
        var dict = new Dictionary<Vector3, int>(estimatedUnique);

        for (int i = 0; i < pixelCount; i += stride)
        {
            int offset = i * 4;
            byte b = bgra8Pixels[offset];
            byte g = bgra8Pixels[offset + 1];
            byte r = bgra8Pixels[offset + 2];
            byte a = bgra8Pixels[offset + 3];
            if (a == 0) continue;

            var color = new Vector3(r, g, b);
            ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(dict, color, out _);
            count++;
        }

        return dict;
    }

    public static async Task<Dictionary<Vector3, int>?> SampleFromBmpCacheAsync(
        string bmpCachePath,
        int stride = 10,
        CancellationToken ct = default)
    {
        if (!File.Exists(bmpCachePath)) return null;

        byte[]? rented = null;
        try
        {
            int w, h, pixelBytes;

            await using (var fs = new FileStream(
                bmpCachePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, useAsync: true))
            {
                if (fs.Length < BmpHeaderSize + 4) return null;

                var header = new byte[BmpHeaderSize];
                await fs.ReadExactlyAsync(header, ct);

                if (header[0] != (byte)'B' || header[1] != (byte)'M')
                    return null;

                w = BitConverter.ToInt32(header[18..22]);
                h = Math.Abs(BitConverter.ToInt32(header[22..26]));
                if (w <= 0 || h <= 0 || w > 4096 || h > 4096)
                    return null;

                pixelBytes = w * h * 4;
                if (fs.Length < BmpHeaderSize + pixelBytes)
                    return null;

                rented = ArrayPool<byte>.Shared.Rent(pixelBytes);
                await fs.ReadExactlyAsync(rented.AsMemory(0, pixelBytes), ct);
            }

            var pixels = new ReadOnlySpan<byte>(rented, 0, pixelBytes);
            return SampleBgra8Pixels(pixels, w, h, stride);
        }
        catch (OperationCanceledException) { return null; }
        catch { return null; }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented, clearArray: false);
        }
    }
}
