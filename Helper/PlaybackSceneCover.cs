using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;

namespace WinUIMusicPlayer.Helper;

/// <summary>Original artwork for the playback scene. Both pixel formats are premultiplied.</summary>
internal sealed record PlaybackSceneCover(byte[] Pixels, int Width, int Height, DirectXPixelFormat Format, float PeakNits)
{
    internal static Task<PlaybackSceneCover?> ReadDefaultAsync(bool isDark, CancellationToken token,
        uint maxPixelSize = PlaybackCoverImage.MaxPixelSize)
    {
        string name = isDark ? "default_cover_black.png" : "default_cover_white.png";
        return ReadAsync(Path.Combine(AppContext.BaseDirectory, "Assets", name), includeSdr: true, token, maxPixelSize);
    }

    internal static Task<PlaybackSceneCover?> ReadPlaybackAsync(string rawPath, CancellationToken token,
        uint maxPixelSize = PlaybackCoverImage.MaxPixelSize) =>
        ReadCoreAsync(rawPath, true, PlaybackCoverImage.GetCachePath(rawPath), token, maxPixelSize);

    internal static Task<PlaybackSceneCover?> ReadAsync(string path, bool includeSdr, CancellationToken token,
        uint maxPixelSize = PlaybackCoverImage.MaxPixelSize) =>
        ReadCoreAsync(path, includeSdr, null, token, maxPixelSize);

    private static async Task<PlaybackSceneCover?> ReadCoreAsync(string path, bool includeSdr, string? cachePath,
        CancellationToken token, uint maxPixelSize)
    {
        token.ThrowIfCancellationRequested();
        bool triedCache = false;
        try
        {
            using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            bool isPq = HasSupportedColorEncoding(input);
            if (!includeSdr && !isPq) return null;
            // A valid display cache is already sized and color-managed. The raw PQ source
            // keeps its highlight information and only uses this cache if decoding fails.
            if (!isPq && cachePath is not null && PlaybackCoverImage.IsCurrent(cachePath, path))
            {
                triedCache = true;
                if (await ReadAsync(cachePath, true, token, maxPixelSize).ConfigureAwait(false) is { } cached)
                    return cached;
            }
            if (await DecodeAsync(input, isPq, token, maxPixelSize).ConfigureAwait(false) is { } artwork)
                return artwork;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException ||
            ((uint)ex.HResult & 0xffffff00u) == 0x88982f00u) // WIC cannot decode this image.
        {
            token.ThrowIfCancellationRequested();
        }
        return cachePath is not null && !triedCache
            ? await ReadAsync(cachePath, true, token, maxPixelSize).ConfigureAwait(false) : null;
    }

    private static async Task<PlaybackSceneCover?> DecodeAsync(Stream input, bool isPq, CancellationToken token, uint maxPixelSize)
    {
        input.Position = 0;
        using var stream = input.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(token).ConfigureAwait(false);
        uint width = decoder.PixelWidth, height = decoder.PixelHeight;
        // Bound the native decoder's working set as well as the final texture.
        if (width == 0 || height == 0 || (ulong)width * height > 64_000_000) return null;
        var transform = ImageHelper.CreateLongestSideTransform(width, height, maxPixelSize);
        uint w = transform.ScaledWidth == 0 ? width : transform.ScaledWidth;
        uint h = transform.ScaledHeight == 0 ? height : transform.ScaledHeight;
        var data = await decoder.GetPixelDataAsync(isPq ? BitmapPixelFormat.Rgba16 : BitmapPixelFormat.Bgra8,
            isPq ? BitmapAlphaMode.Straight : BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
            isPq ? ColorManagementMode.DoNotColorManage : ColorManagementMode.ColorManageToSRgb)
            .AsTask(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var pixels = data.DetachPixelData();
        if (pixels.Length != (long)w * h * (isPq ? 8 : 4)) return null;
        if (width != height && decoder.OrientedPixelWidth == height) (w, h) = (h, w);
        return isPq
            ? new(pixels, (int)w, (int)h, DirectXPixelFormat.R16G16B16A16Float, ConvertInPlace(pixels, token))
            : new(pixels, (int)w, (int)h, DirectXPixelFormat.B8G8R8A8UIntNormalized, 0);
    }

    private static bool HasSupportedColorEncoding(Stream input)
    {
        Span<byte> header = stackalloc byte[8];
        if (input.Length < 33) return false;
        input.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt64BigEndian(header) != 0x89504e470d0a1a0aUL) return false;
        // PNG cICP precedes IDAT. Skip ancillary chunks by length without reading their payload.
        for (int count = 0; count < 4096 && input.Length - input.Position >= 12; count++)
        {
            input.ReadExactly(header);
            uint length = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (length + 4L > input.Length - input.Position || header[4..].SequenceEqual("IDAT"u8)) return false;
            if (header[4..].SequenceEqual("cICP"u8))
            {
                if (length != 4) return false;
                input.ReadExactly(header);
                // Includes the PNG CRC for the exact supported cICP payload.
                return BinaryPrimitives.ReadUInt32BigEndian(header) == 0x09100001 &&
                    BinaryPrimitives.ReadUInt32BigEndian(header[4..]) == CrcForBt2020Pq;
            }
            input.Seek(length + 4L, SeekOrigin.Current);
        }
        return false;
    }

    private const uint CrcForBt2020Pq = 0x4d2323feu;

    private static readonly Lazy<float[]> PqTable = new(CreatePqTable);

    private static float[] CreatePqTable()
    {
        var table = new float[65536];
        for (int i = 0; i < table.Length; i++)
        {
            double p = Math.Pow(i / 65535.0, 32.0 / 2523.0);
            table[i] = (float)(125.0 * Math.Pow(Math.Max(p - 3424.0 / 4096, 0) /
                (2413.0 / 128 - 2392.0 / 128 * p), 16384.0 / 2610));
        }
        return table;
    }

    private static float ConvertInPlace(byte[] pixels, CancellationToken token)
    {
        var codes = MemoryMarshal.Cast<byte, ushort>(pixels);
        var output = MemoryMarshal.Cast<byte, Half>(pixels);
        var table = PqTable.Value;
        float peak = 0;
        for (int i = 0; i < codes.Length; i += 4)
        {
            if ((i & 0xffff) == 0) token.ThrowIfCancellationRequested();
            float r = table[codes[i]], g = table[codes[i + 1]], b = table[codes[i + 2]];
            float a = codes[i + 3] / 65535f;
            // BT.2020 -> BT.709, both use D65. Negative scRGB values preserve wide-gamut colors.
            output[i] = (Half)((1.660491f * r - 0.587641f * g - 0.072850f * b) * a);
            output[i + 1] = (Half)((-0.124550f * r + 1.132900f * g - 0.008349f * b) * a);
            output[i + 2] = (Half)((-0.018151f * r - 0.100579f * g + 1.118730f * b) * a);
            output[i + 3] = (Half)a;
            if (a > 0) peak = Math.Max(peak, 80 * (0.2627f * r + 0.6780f * g + 0.0593f * b));
        }
        return peak;
    }
}
