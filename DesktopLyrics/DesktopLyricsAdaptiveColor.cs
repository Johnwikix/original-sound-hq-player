using Microsoft.UI;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.UI;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词环境自适应取色（参考 BetterLyrics ColorHelper 的 WindowEdge 模式）：
    /// GDI 从屏幕 DC 拉取悬浮窗外围一圈（36px 环带，避开窗口自身歌词字形）的像素，
    /// 过滤低饱和/极端亮度像素后量化统计主色作为环境底色，
    /// 再按 YIQ 亮度推导可读文字色：亮背景用黑色，暗背景用白色。
    /// 低频（1s 轮询）在 UI 线程调用。
    /// </summary>
    internal static class DesktopLyricsAdaptiveColor
    {
        private const int EdgeThickness = 36;   // 采样环带厚度（物理像素）
        private const int SampleSize = 64;      // 每条环带拉伸到的采样边长
        private const int RasterOpSrcCopy = 0x00CC0020;
        private const int StretchBltColorOnColor = 3;   // COLORONCOLOR
        private const int DibRgbColors = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFO
        {
            public BITMAPINFOHEADER bmiHeader;
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO pbmi, int iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [DllImport("gdi32.dll")]
        private static extern int SetStretchBltMode(IntPtr hdc, int iStretchMode);

        [DllImport("gdi32.dll")]
        private static extern bool StretchBlt(IntPtr hdcDest, int xDest, int yDest, int wDest, int hDest, IntPtr hdcSrc, int xSrc, int ySrc, int wSrc, int hSrc, int rop);

        /// <summary>
        /// 采样悬浮窗周围环境并推导歌词文字颜色。窗口移动/背景变化由调用方轮询覆盖；
        /// 失败（如取不到窗口矩形）返回 false，调用方保持当前颜色。
        /// </summary>
        public static bool TryGetAdaptiveTextColor(IntPtr hwnd, out Color textColor)
        {
            textColor = default;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return false;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            IntPtr hdcScreen = GetDC(IntPtr.Zero);
            if (hdcScreen == IntPtr.Zero) return false;
            try
            {
                IntPtr hdcMem = CreateCompatibleDC(hdcScreen);
                if (hdcMem == IntPtr.Zero) return false;
                try
                {
                    if (!TryCreateSampleDib(hdcMem, out IntPtr hBitmap, out IntPtr bits)) return false;
                    // 位图必须选入内存 DC，否则 StretchBlt 落在默认 1x1 单色占位图上，DIB 缓冲恒为全零
                    IntPtr oldBitmap = SelectObject(hdcMem, hBitmap);
                    try
                    {
                        SetStretchBltMode(hdcMem, StretchBltColorOnColor);
                        var stats = new DominantColorStats();
                        var pixels = new int[SampleSize * SampleSize];

                        // 上下左右四条环带依次整幅拉伸进 64x64 采样位并就地累计（每条环带权重一致）
                        bool Accumulate(int x, int y, int w, int h)
                        {
                            if (!StretchBlt(hdcMem, 0, 0, SampleSize, SampleSize, hdcScreen, x, y, w, h, RasterOpSrcCopy)) return false;
                            Marshal.Copy(bits, pixels, 0, pixels.Length);
                            stats.Add(pixels);
                            return true;
                        }

                        bool sampled =
                            Accumulate(rect.Left, rect.Top - EdgeThickness, width, EdgeThickness) &&
                            Accumulate(rect.Left, rect.Bottom, width, EdgeThickness) &&
                            Accumulate(rect.Left - EdgeThickness, rect.Top, EdgeThickness, height) &&
                            Accumulate(rect.Right, rect.Top, EdgeThickness, height);
                        if (!sampled) return false;

                        Color underlay = stats.ComputeDominantColor();
                        if (underlay.A == 0) return false;
                        textColor = GetTextColorFor(underlay);
                        return true;
                    }
                    finally
                    {
                        SelectObject(hdcMem, oldBitmap);
                        DeleteObject(hBitmap);
                    }
                }
                finally
                {
                    DeleteDC(hdcMem);
                }
            }
            finally
            {
                ReleaseDC(IntPtr.Zero, hdcScreen);
            }
        }

        /// <summary>按 YIQ 亮度选择可读文字色：亮背景用黑色，暗背景用白色。</summary>
        public static Color GetTextColorFor(Color background)
        {
            double yiq = ((background.R * 299) + (background.G * 587) + (background.B * 114)) / 1000.0;
            return yiq >= 128 ? Colors.Black : Colors.White;
        }

        private static bool TryCreateSampleDib(IntPtr hdcMem, out IntPtr hBitmap, out IntPtr bits)
        {
            hBitmap = IntPtr.Zero;
            bits = IntPtr.Zero;
            var bmi = new BITMAPINFO();
            bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>();
            bmi.bmiHeader.biWidth = SampleSize;
            bmi.bmiHeader.biHeight = -SampleSize;   // top-down，缓冲区首行对应位图顶行
            bmi.bmiHeader.biPlanes = 1;
            bmi.bmiHeader.biBitCount = 32;          // BI_RGB 32bpp：int 布局 0xAARRGGBB
            hBitmap = CreateDIBSection(hdcMem, ref bmi, DibRgbColors, out bits, IntPtr.Zero, 0);
            return hBitmap != IntPtr.Zero && bits != IntPtr.Zero;
        }

        /// <summary>BetterLyrics ComputeDominantColor 同款：过滤低饱和/极端亮度像素，
        /// RGB 高位量化后按频次取主色；无主色（如纯色/黑白壁纸）回退平均色。</summary>
        private sealed class DominantColorStats
        {
            private readonly Dictionary<int, int> _frequencies = new(64);
            private long _sumR, _sumG, _sumB;
            private int _total, _dominant, _maxFrequency;

            public void Add(int[] pixels)
            {
                foreach (int pixel in pixels)
                {
                    int r = (pixel >> 16) & 0xFF;
                    int g = (pixel >> 8) & 0xFF;
                    int b = pixel & 0xFF;
                    _sumR += r;
                    _sumG += g;
                    _sumB += b;
                    _total++;

                    int max = Math.Max(r, Math.Max(g, b));
                    int min = Math.Min(r, Math.Min(g, b));
                    int saturation = max == 0 ? 0 : (max - min) * 255 / max;
                    // 过滤低饱和度或接近纯黑/纯白的像素
                    if (saturation < 30 || max < 30 || max > 240) continue;

                    int quantized = ((r & 0xF0) << 16) | ((g & 0xF0) << 8) | (b & 0xF0);
                    _frequencies.TryGetValue(quantized, out int count);
                    _frequencies[quantized] = ++count;
                    if (count > _maxFrequency)
                    {
                        _maxFrequency = count;
                        _dominant = quantized;
                    }
                }
            }

            public Color ComputeDominantColor()
            {
                if (_total == 0) return Colors.Transparent;
                if (_maxFrequency == 0)
                {
                    return Color.FromArgb(0xFF,
                        (byte)(_sumR / _total), (byte)(_sumG / _total), (byte)(_sumB / _total));
                }
                return Color.FromArgb(0xFF,
                    (byte)Math.Min(255, ((_dominant >> 16) & 0xFF) + 8),
                    (byte)Math.Min(255, ((_dominant >> 8) & 0xFF) + 8),
                    (byte)Math.Min(255, (_dominant & 0xFF) + 8));
            }
        }
    }
}
