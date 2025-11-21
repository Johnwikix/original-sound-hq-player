using Microsoft.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using ZLinq;

namespace WinUIMusicPlayer.Extensions
{
    public static class ImageHelper
    {
        #region 处理模糊图像

        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipBitmapApplyEffect(IntPtr bitmap, IntPtr effect, ref Rectangle rectOfInterest,
            bool useAuxData, IntPtr auxData, int auxDataSize);

        /// <summary>
        /// 获取对象的私有字段的值
        /// </summary>
        internal static TResult GetPrivateField<TResult>(this object obj, string fieldName)
        {
            if (obj == null) return default(TResult);
            Type ltType = obj.GetType();
            FieldInfo lfiFieldInfo = ltType.GetField(fieldName,
                BindingFlags.GetField | BindingFlags.Instance | BindingFlags.NonPublic);
            if (lfiFieldInfo != null)
                return (TResult)lfiFieldInfo.GetValue(obj);
            else
                throw new InvalidOperationException(string.Format(
                    "Instance field '{0}' could not be located in object of type '{1}'.", fieldName,
                    obj.GetType().FullName));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BlurParameters
        {
            internal float Radius;
            internal bool ExpandEdges;
        }

        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipCreateEffect(Guid guid, out IntPtr effect);

        private static Guid BlurEffectGuid = new Guid("{633C80A4-1843-482B-9EF2-BE2834C5FDD4}");

        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipSetEffectParameters(IntPtr effect, IntPtr parameters, uint size);

        public static IntPtr NativeHandle(this Bitmap Bmp)
        {
            // 尝试不同版本的字段名
            // 新版本（System.Drawing.Common 7.0+）使用 _nativeImage
            // 旧版本使用 nativeImage

            Type bitmapType = Bmp.GetType();

            // 尝试新版本字段名
            FieldInfo field = bitmapType.GetField("_nativeImage",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // 如果找不到，尝试旧版本字段名
            if (field == null)
            {
                field = bitmapType.GetField("nativeImage",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }

            // 如果还是找不到，尝试在基类中查找
            if (field == null && bitmapType.BaseType != null)
            {
                field = bitmapType.BaseType.GetField("_nativeImage",
                    BindingFlags.NonPublic | BindingFlags.Instance);

                if (field == null)
                {
                    field = bitmapType.BaseType.GetField("nativeImage",
                        BindingFlags.NonPublic | BindingFlags.Instance);
                }
            }

            if (field != null)
            {
                var value = field.GetValue(Bmp);

                // 处理不同的返回类型
                if (value is IntPtr ptr)
                {
                    return ptr;
                }
                else if (value != null && value.GetType().Name == "Pointer")
                {
                    // 使用 Pointer.Unbox 处理 System.Reflection.Pointer 类型
                    unsafe
                    {
                        return new IntPtr(Pointer.Unbox(value));
                    }
                }
            }

            throw new InvalidOperationException(
                $"无法获取 Bitmap 的 GDI+ 内部句柄。当前 System.Drawing 版本可能不支持。" +
                $"Bitmap 类型: {bitmapType.FullName}");
        }

        [DllImport("gdiplus.dll", SetLastError = true, ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int GdipDeleteEffect(IntPtr effect);

        public static void GaussianBlur(this Bitmap Bmp, ref Rectangle Rect, float Radius = 10, bool ExpandEdge = false)
        {
            int Result;
            IntPtr BlurEffect;
            BlurParameters BlurPara;
            if ((Radius < 0) || (Radius > 255))
            {
                throw new ArgumentOutOfRangeException("半径必须在[0,255]范围内");
            }

            BlurPara.Radius = Radius;
            BlurPara.ExpandEdges = ExpandEdge;
            Result = GdipCreateEffect(BlurEffectGuid, out BlurEffect);
            if (Result == 0)
            {
                IntPtr Handle = Marshal.AllocHGlobal(Marshal.SizeOf(BlurPara));
                Marshal.StructureToPtr(BlurPara, Handle, true);
                GdipSetEffectParameters(BlurEffect, Handle, (uint)Marshal.SizeOf(BlurPara));
                GdipBitmapApplyEffect(Bmp.NativeHandle(), BlurEffect, ref Rect, false, IntPtr.Zero, 0);
                GdipDeleteEffect(BlurEffect);
                Marshal.FreeHGlobal(Handle);
            }
            else
            {
                throw new ExternalException("不支持的GDI+版本，必须为GDI+1.1及以上版本，且操作系统要求为Win Vista及之后版本.");
            }
        }

        #endregion

        #region 颜色提取和处理

        public static Windows.UI.Color GetMajorColor(this Bitmap bitmap)
        {
            int samplingRate = Math.Max(1, Math.Min(bitmap.Width, bitmap.Height) / 100);

            Dictionary<int, ColorInfo> colorMap = new();
            int pixelCount = 0;

            for (int h = 0; h < bitmap.Height; h += samplingRate)
            {
                for (int w = 0; w < bitmap.Width; w += samplingRate)
                {
                    Color pixel = bitmap.GetPixel(w, h);

                    int quantizedR = pixel.R / 16 * 16;
                    int quantizedG = pixel.G / 16 * 16;
                    int quantizedB = pixel.B / 16 * 16;

                    int averange = (pixel.R + pixel.G + pixel.B) / 3;
                    if (averange < 24) continue;
                    if (averange > 230) continue;

                    int colorKey = (quantizedR << 16) | (quantizedG << 8) | quantizedB;

                    if (colorMap.TryGetValue(colorKey, out ColorInfo info))
                    {
                        info.Count++;
                        info.SumR += pixel.R;
                        info.SumG += pixel.G;
                        info.SumB += pixel.B;
                    }
                    else
                    {
                        colorMap[colorKey] = new ColorInfo
                        {
                            Count = 1,
                            SumR = pixel.R,
                            SumG = pixel.G,
                            SumB = pixel.B
                        };
                    }

                    pixelCount++;
                }
            }

            if (pixelCount == 0 || colorMap.Count == 0)
                return Colors.Gray;

            var weightedColors = colorMap.Values.AsValueEnumerable().Select(info =>
            {
                float r = info.SumR / (float)info.Count / 255f;
                float g = info.SumG / (float)info.Count / 255f;
                float b = info.SumB / (float)info.Count / 255f;

                RgbToHsl(r, g, b, out float h, out float s, out float l);

                float weight = info.Count * s * (1 - Math.Abs(l - 0.6f) * 1.8f);

                return new
                {
                    R = info.SumR / info.Count,
                    G = info.SumG / info.Count,
                    B = info.SumB / info.Count,
                    Weight = weight
                };
            })
            .OrderByDescending(c => c.Weight)
            .ToList();

            if (weightedColors.Count > 0)
            {
                var dominantColor = weightedColors[0];
                return Windows.UI.Color.FromArgb(255,
                    (byte)dominantColor.R,
                    (byte)dominantColor.G,
                    (byte)dominantColor.B);
            }

            return Colors.Gray;
        }

        private class ColorInfo
        {
            public int Count { get; set; }
            public int SumR { get; set; }
            public int SumG { get; set; }
            public int SumB { get; set; }
        }

        public static Windows.UI.Color AdjustColor(this Windows.UI.Color col, bool isDarkMode)
        {
            RgbToHsl(col.R / 255f, col.G / 255f, col.B / 255f, out float h, out float s, out float l);

            bool isNearGrayscale = s < 0.15f;

            if (isDarkMode)
            {
                if (l < 0.5f)
                {
                    l = 0.3f + l * 0.5f;
                }

                if (isNearGrayscale)
                {
                    l = Math.Max(l, 0.4f);
                }
            }
            else
            {
                if (l > 0.5f)
                {
                    l = 0.3f + l * 0.4f;
                }

                if (isNearGrayscale)
                {
                    l = Math.Min(l, 0.6f);
                }
            }

            if (!isNearGrayscale)
            {
                if (s > 0.7f)
                {
                    s = isDarkMode ? 0.7f - (s - 0.7f) * 0.2f : 0.65f - (s - 0.7f) * 0.4f;
                }
                else if (s > 0.4f)
                {
                    s = isDarkMode ? s * 0.85f : s * 0.75f;
                }
                else if (s > 0.1f)
                {
                    s = isDarkMode ? Math.Min(0.5f, s * 1.5f) : Math.Min(0.4f, s * 1.3f);
                }
            }

            if (!isNearGrayscale)
            {
                if ((h <= 0.08f) || (h >= 0.92f))
                {
                    if (isDarkMode)
                    {
                        s = Math.Min(0.7f, s * 1.1f);
                        l = Math.Min(0.8f, l * 1.15f);
                    }
                    else
                    {
                        s *= 0.8f;
                        l = Math.Max(0.4f, l * 0.9f);
                    }
                }
                else if (h >= 0.25f && h <= 0.42f)
                {
                    if (isDarkMode)
                    {
                        s *= 0.85f;
                        l = Math.Min(0.7f, l * 1.2f);
                    }
                    else
                    {
                        s *= 0.75f;
                    }
                }
                else if (h >= 0.58f && h <= 0.75f)
                {
                    if (isDarkMode)
                    {
                        s = Math.Min(0.85f, s * 1.2f);
                        l = Math.Min(0.7f, l * 1.25f);
                    }
                    else
                    {
                        s = Math.Min(0.7f, Math.Max(0.4f, s));
                    }
                }
                else if (h > 0.08f && h < 0.25f)
                {
                    if (isDarkMode)
                    {
                        s *= 0.8f;
                        l = Math.Min(0.75f, l * 1.2f);
                    }
                    else
                    {
                        s *= 0.7f;
                        l = Math.Max(0.5f, l * 0.9f);
                    }
                }
            }

            if (isDarkMode && l < 0.3f) l = 0.3f;
            if (!isDarkMode && l > 0.7f) l = 0.7f;
            l = 0.2f;
            HslToRgb(h, s, l, out float r, out float g, out float b);

            byte R = (byte)Math.Max(0, Math.Min(255, r * 255));
            byte G = (byte)Math.Max(0, Math.Min(255, g * 255));
            byte B = (byte)Math.Max(0, Math.Min(255, b * 255));

            return Windows.UI.Color.FromArgb(255, R, G, B);
        }

        public static Windows.UI.Color ApplyColorMode(this Windows.UI.Color color, bool isDarkMode)
        {
            RgbToHsl(color.R / 255f, color.G / 255f, color.B / 255f, out float h, out float s, out float l);
            if (isDarkMode)
                l = Math.Max(0.05f, l - 0.1f);
            else
                l = Math.Min(0.95f, l + 0.1f);

            HslToRgb(h, s, l, out float r, out float g, out float b);
            return Windows.UI.Color.FromArgb(255, (byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
        }

        #endregion

        #region HSL 转换

        private static void RgbToHsl(float r, float g, float b, out float h, out float s, out float l)
        {
            float max = Math.Max(r, Math.Max(g, b));
            float min = Math.Min(r, Math.Min(g, b));

            l = (max + min) / 2.0f;

            h = 0;
            s = 0;

            if (max == min)
            {
                return;
            }

            float d = max - min;

            s = l > 0.5f ? d / (2.0f - max - min) : d / (max + min);

            if (max == r)
            {
                h = (g - b) / d + (g < b ? 6.0f : 0.0f);
            }
            else if (max == g)
            {
                h = (b - r) / d + 2.0f;
            }
            else
            {
                h = (r - g) / d + 4.0f;
            }

            h /= 6.0f;
            h = Math.Max(0, Math.Min(1, h));
        }

        private static void HslToRgb(float h, float s, float l, out float r, out float g, out float b)
        {
            h = ((h % 1.0f) + 1.0f) % 1.0f;
            s = Math.Max(0, Math.Min(1, s));
            l = Math.Max(0, Math.Min(1, l));

            if (s == 0.0f)
            {
                r = g = b = l;
                return;
            }

            float q = l < 0.5f ? l * (1.0f + s) : l + s - l * s;
            float p = 2.0f * l - q;

            r = HueToRgb(p, q, h + 1.0f / 3.0f);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0f / 3.0f);
        }

        private static float HueToRgb(float p, float q, float t)
        {
            t = ((t % 1.0f) + 1.0f) % 1.0f;

            if (t < 1.0f / 6.0f)
                return p + (q - p) * 6.0f * t;
            if (t < 0.5f)
                return q;
            if (t < 2.0f / 3.0f)
                return p + (q - p) * (2.0f / 3.0f - t) * 6.0f;
            return p;
        }

        #endregion

        #region 图像转换

        public static async Task<WriteableBitmap> ToBitmapImageAsync(this Bitmap bitmap)
        {
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            memory.Position = 0;

            var writeableBitmap = new WriteableBitmap(bitmap.Width, bitmap.Height);
            await writeableBitmap.SetSourceAsync(memory.AsRandomAccessStream());
            return writeableBitmap;
        }

        public static async Task<Bitmap> ToBitmapAsync(this WriteableBitmap writeableBitmap)
        {
            using var stream = new InMemoryRandomAccessStream();
            BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);

            Stream pixelStream = writeableBitmap.PixelBuffer.AsStream();
            byte[] pixels = new byte[pixelStream.Length];
            await pixelStream.ReadAsync(pixels, 0, pixels.Length);

            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)writeableBitmap.PixelWidth,
                (uint)writeableBitmap.PixelHeight,
                96.0,
                96.0,
                pixels);

            await encoder.FlushAsync();

            stream.Seek(0);
            return new Bitmap(stream.AsStream());
        }

        #endregion

        #region 图像效果

        public static void AddMask(this Bitmap bitmap, bool darkmode)
        {
            var color1 = darkmode ? Color.FromArgb(90, 0, 0, 0) : Color.FromArgb(90, 255, 255, 255);
            var color2 = darkmode ? Color.FromArgb(120, 0, 0, 0) : Color.FromArgb(120, 255, 255, 255);
            using Graphics g = Graphics.FromImage(bitmap);
            using LinearGradientBrush brush = new(
                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                color1,
                color2,
                LinearGradientMode.Vertical);
            g.FillRectangle(brush, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
        }

        public static void AdjustContrast(this Bitmap bitmap, float contrast)
        {
            contrast = (100.0f + contrast) / 100.0f;
            contrast *= contrast;

            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadWrite, bitmap.PixelFormat);

            int width = bitmap.Width;
            int height = bitmap.Height;

            unsafe
            {
                for (int y = 0; y < height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (y * data.Stride);
                    for (int x = 0; x < width; x++)
                    {
                        int idx = x * 3;

                        float blue = row[idx] / 255.0f;
                        float green = row[idx + 1] / 255.0f;
                        float red = row[idx + 2] / 255.0f;

                        RgbToHsl(red, green, blue, out float h, out float s, out float l);

                        l = (((l - 0.5f) * contrast) + 0.5f);

                        HslToRgb(h, s, l, out red, out green, out blue);

                        row[idx] = (byte)Math.Max(0, Math.Min(255, blue * 255.0f));
                        row[idx + 1] = (byte)Math.Max(0, Math.Min(255, green * 255.0f));
                        row[idx + 2] = (byte)Math.Max(0, Math.Min(255, red * 255.0f));
                    }
                }
            }

            bitmap.UnlockBits(data);
        }

        public static void ScaleImage(this Bitmap bitmap, double scale)
        {
            int newWidth = (int)(bitmap.Width * scale);
            int newHeight = (int)(bitmap.Height * scale);

            Bitmap newBitmap = new Bitmap(newWidth, newHeight, bitmap.PixelFormat);

            using (Graphics graphics = Graphics.FromImage(newBitmap))
            {
                graphics.CompositingQuality = CompositingQuality.HighQuality;
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.SmoothingMode = SmoothingMode.HighQuality;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                graphics.DrawImage(bitmap,
                    new Rectangle(0, 0, newWidth, newHeight),
                    new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    GraphicsUnit.Pixel);
            }

            bitmap = newBitmap;
        }

        public static void ApplyMicaEffect(this Bitmap bitmap, bool isDarkmode)
        {
            bitmap.AdjustContrast(isDarkmode ? -1 : -20);
            bitmap.AddMask(isDarkmode);
            bitmap.ScaleImage(2);
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            bitmap.GaussianBlur(ref rect, 80f, false);
        }

        #endregion
    }
}
