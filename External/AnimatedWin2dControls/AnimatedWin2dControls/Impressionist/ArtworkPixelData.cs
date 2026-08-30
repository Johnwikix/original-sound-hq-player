using System;
using System.Buffers;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimatedWin2dControls.Impressionist
{
    /// <summary>
    /// 已解码的方形封面像素，固定 <see cref="Edge"/>×<see cref="Edge"/>、
    /// 直通（非预乘）RGBA8 行优先、自上而下。专供 AppleMusic 背景着色器的
    /// D2D1ResourceTextureManager 上传使用。引用类型：跨线程仅做整体引用交换。
    /// </summary>
    public sealed class ArtworkPixelData
    {
        public const int Edge = 128;

        public ArtworkPixelData(byte[] pixels)
        {
            Pixels = pixels;
        }

        /// <summary>长度恒为 Edge*Edge*4 的 RGBA8 像素缓冲。</summary>
        public byte[] Pixels { get; }
    }

    /// <summary>
    /// 从封面缩略图 BMP 缓存（见主工程 CoverLoadQueue：8B 对齐头 + Bgra8 预乘裸像素、
    /// 自上而下）解码出固定尺寸的方形 <see cref="ArtworkPixelData"/>：
    /// 中心裁方 + 双线性缩放到 <see cref="ArtworkPixelData.Edge"/>²。
    /// 固定尺寸保证着色器侧资源纹理只创建一次，换歌仅需 <c>Update</c>。
    /// </summary>
    public static class ArtworkPixelDecoder
    {
        private const int BmpHeaderSize = 54;

        public static async Task<ArtworkPixelData?> LoadSquareRgba8FromBmpCacheAsync(
            string? bmpCachePath,
            int edge = ArtworkPixelData.Edge,
            CancellationToken ct = default)
        {
            if (string.IsNullOrEmpty(bmpCachePath) || !File.Exists(bmpCachePath)) return null;

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

                    if (header[0] != (byte)'B' || header[1] != (byte)'M') return null;

                    w = BitConverter.ToInt32(header[18..22]);
                    h = Math.Abs(BitConverter.ToInt32(header[22..26]));
                    if (w <= 0 || h <= 0 || w > 4096 || h > 4096) return null;

                    pixelBytes = w * h * 4;
                    if (fs.Length < BmpHeaderSize + pixelBytes) return null;

                    rented = ArrayPool<byte>.Shared.Rent(pixelBytes);
                    await fs.ReadExactlyAsync(rented.AsMemory(0, pixelBytes), ct);
                }

                return ConvertToSquareRgba8(rented, w, h, edge);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (rented is not null)
                    ArrayPool<byte>.Shared.Return(rented, clearArray: false);
            }
        }

        /// <summary>BGRA8（预乘）→ 中心裁方 → 双线性缩放 → RGBA8（直通）。</summary>
        private static ArtworkPixelData ConvertToSquareRgba8(byte[] bgra, int w, int h, int edge)
        {
            int side = Math.Min(w, h);
            int srcX = (w - side) / 2;
            int srcY = (h - side) / 2;

            float scale = side / (float)edge;
            var result = new byte[edge * edge * 4];

            for (int y = 0; y < edge; y++)
            {
                // 最近邻在 cover 尺寸接近 edge 时已足够平滑；下方采样按双线性处理浮点落点。
                float sy = MathF.Min((y + 0.5f) * scale - 0.5f, side - 1f);
                int y0 = (int)MathF.Floor(sy);
                int y1 = Math.Min(y0 + 1, side - 1);
                float fy = sy - y0;
                if (y0 < 0) { y0 = 0; fy = 0f; }

                for (int x = 0; x < edge; x++)
                {
                    float sx = MathF.Min((x + 0.5f) * scale - 0.5f, side - 1f);
                    int x0 = (int)MathF.Floor(sx);
                    int x1 = Math.Min(x0 + 1, side - 1);
                    float fx = sx - x0;
                    if (x0 < 0) { x0 = 0; fx = 0f; }

                    SampleBgra(bgra, w, srcX + x0, srcY + y0, out float b00, out float g00, out float r00, out float a00);
                    SampleBgra(bgra, w, srcX + x1, srcY + y0, out float b10, out float g10, out float r10, out float a10);
                    SampleBgra(bgra, w, srcX + x0, srcY + y1, out float b01, out float g01, out float r01, out float a01);
                    SampleBgra(bgra, w, srcX + x1, srcY + y1, out float b11, out float g11, out float r11, out float a11);

                    int dst = (y * edge + x) * 4;

                    result[dst] = (byte)Math.Round(Bilinear(r00, r10, r01, r11, fx, fy));
                    result[dst + 1] = (byte)Math.Round(Bilinear(g00, g10, g01, g11, fx, fy));
                    result[dst + 2] = (byte)Math.Round(Bilinear(b00, b10, b01, b11, fx, fy));

                    // 源缓存为预乘 alpha；缩放前按直通处理需还原。封面几乎全为不透明，
                    // 仅对半透明像素做代价可控的除法还原。
                    float a = Bilinear(a00, a10, a01, a11, fx, fy);
                    if (a > 0.5f && a < 254.5f)
                    {
                        float k = 255f / a;
                        result[dst] = (byte)Math.Min(255f, result[dst] * k);
                        result[dst + 1] = (byte)Math.Min(255f, result[dst + 1] * k);
                        result[dst + 2] = (byte)Math.Min(255f, result[dst + 2] * k);
                    }
                    result[dst + 3] = 255;
                }
            }

            return new ArtworkPixelData(result);
        }

        private static void SampleBgra(byte[] bgra, int w, int x, int y, out float b, out float g, out float r, out float a)
        {
            int o = (y * w + x) * 4;
            b = bgra[o];
            g = bgra[o + 1];
            r = bgra[o + 2];
            a = bgra[o + 3];
        }

        private static float Bilinear(float c00, float c10, float c01, float c11, float fx, float fy)
        {
            float top = c00 + (c10 - c00) * fx;
            float bottom = c01 + (c11 - c01) * fx;
            return top + (bottom - top) * fy;
        }
    }
}
