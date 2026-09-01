using System;
using System.Runtime.InteropServices;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词环境亮度采样（BetterLyrics WindowEdge 思路的改造版）：
    /// GDI 从屏幕 DC 拉取悬浮窗外围一圈（36px 环带，避开窗口自身歌词字形）的像素，
    /// 统计 YIQ 亮度直方图取中位数，宿主窗口按阈值（带滞回）切换黑/白文字色。
    /// 刻意不做"主色频次统计"：白色网页等文字类背景上，白色背景与文字主体都会被
    /// 低饱和/极端亮度过滤剔除，幸存占频次优势的是文字反锯齿的中灰像素，
    /// 主色会被误判成深色（表现为白底白字、拖动后忽好忽坏）。
    /// 亮度中位数对少数派文字/图标像素稳健，直接贴合"背景整体亮不亮"的语义。
    /// 低频（1s 轮询）在 UI 线程调用。
    /// </summary>
    internal static class DesktopLyricsAdaptiveColor
    {
        private const int EdgeThickness = 36;   // 采样环带厚度（物理像素）
        private const int SampleSize = 64;      // 每条环带拉伸到的采样边长
        private const int RasterOpSrcCopy = 0x00CC0020;
        private const int StretchBltColorOnColor = 3;   // COLORONCOLOR
        private const int DibRgbColors = 0;
        private const int SmXVirtualScreen = 76;
        private const int SmYVirtualScreen = 77;
        private const int SmCxVirtualScreen = 78;
        private const int SmCyVirtualScreen = 79;

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

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

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
        /// 采样悬浮窗周围环境的 YIQ 亮度中位数（0=纯黑，255=纯白）。
        /// 环带先裁剪进虚拟屏幕（贴边/跨屏窗口不把屏外黑像素计进亮度），
        /// 全部环带都在屏外（如窗口最小化）时返回 false，调用方保持当前颜色。
        /// </summary>
        public static bool TrySampleBackgroundLuminance(IntPtr hwnd, out double luminance)
        {
            luminance = 0;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT rect)) return false;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return false;

            int vsLeft = GetSystemMetrics(SmXVirtualScreen);
            int vsTop = GetSystemMetrics(SmYVirtualScreen);
            int vsRight = vsLeft + GetSystemMetrics(SmCxVirtualScreen);
            int vsBottom = vsTop + GetSystemMetrics(SmCyVirtualScreen);

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
                        var histogram = new int[256];
                        var pixels = new int[SampleSize * SampleSize];
                        long total = 0;

                        // 上下左右四条环带依次整幅拉伸进 64x64 采样位并就地累计（每条环带权重一致）
                        bool Accumulate(int x, int y, int w, int h)
                        {
                            int cx = Math.Max(x, vsLeft);
                            int cy = Math.Max(y, vsTop);
                            int right = Math.Min(x + w, vsRight);
                            int bottom = Math.Min(y + h, vsBottom);
                            if (right <= cx || bottom <= cy) return true;   // 整条在虚拟屏外，跳过（不算失败）
                            if (!StretchBlt(hdcMem, 0, 0, SampleSize, SampleSize, hdcScreen, cx, cy, right - cx, bottom - cy, RasterOpSrcCopy)) return false;
                            Marshal.Copy(bits, pixels, 0, pixels.Length);
                            foreach (int pixel in pixels)
                            {
                                int r = (pixel >> 16) & 0xFF;
                                int g = (pixel >> 8) & 0xFF;
                                int b = pixel & 0xFF;
                                histogram[(r * 299 + g * 587 + b * 114) / 1000]++;
                                total++;
                            }
                            return true;
                        }

                        bool sampled =
                            Accumulate(rect.Left, rect.Top - EdgeThickness, width, EdgeThickness) &&
                            Accumulate(rect.Left, rect.Bottom, width, EdgeThickness) &&
                            Accumulate(rect.Left - EdgeThickness, rect.Top, EdgeThickness, height) &&
                            Accumulate(rect.Right, rect.Top, EdgeThickness, height);
                        if (!sampled || total == 0) return false;

                        // 直方图中位数：少数派像素（文字/图标/角落元素）拉不动它，
                        // 比平均色/主色更贴近"背景整体亮暗"，边界也不会随内容小幅波动
                        long half = total / 2;
                        long cumulative = 0;
                        int median = 255;
                        for (int value = 0; value < 256; value++)
                        {
                            cumulative += histogram[value];
                            if (cumulative > half)
                            {
                                median = value;
                                break;
                            }
                        }
                        luminance = median;
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
    }
}
