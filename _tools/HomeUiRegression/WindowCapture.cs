using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Imaging;

/// <summary>捕获本测试窗口的实际渲染帧，包含 RenderTargetBitmap 略过的合成光照。</summary>
internal static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out Rectangle rectangle);
    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, byte[] bytes, ref BitmapHeader header, uint usage);

    internal sealed record Frame(int Width, int Height, byte[] Pixels);

    internal static Frame ReadFrame(Window window)
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(window);
        if (!GetClientRect(handle, out var rectangle)) throw new InvalidOperationException("GetClientRect failed");
        int width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top;
        nint dc = GetDC(handle);
        if (dc == 0) throw new InvalidOperationException("GetDC failed");
        nint memory = 0, bitmap = 0, previous = 0;
        var pixels = new byte[width * height * 4];
        try
        {
            memory = CreateCompatibleDC(dc);
            if (memory == 0) throw new InvalidOperationException("CreateCompatibleDC failed");
            bitmap = CreateCompatibleBitmap(dc, width, height);
            if (bitmap == 0) throw new InvalidOperationException("CreateCompatibleBitmap failed");
            previous = SelectObject(memory, bitmap);
            if (!PrintWindow(handle, memory, 3)) throw new InvalidOperationException("PrintWindow failed");
            SelectObject(memory, previous);
            previous = 0;
            var header = new BitmapHeader { Size = (uint)Marshal.SizeOf<BitmapHeader>(), Width = width, Height = -height, Planes = 1, BitCount = 32 };
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0) throw new InvalidOperationException("GetDIBits failed");
        }
        finally
        {
            if (previous != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(handle, dc);
        }
        return new Frame(width, height, pixels);
    }

    internal static async Task SaveAsync(Frame frame, string path)
    {
        using var stream = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, (uint)frame.Width, (uint)frame.Height, 96, 96, frame.Pixels);
        await encoder.FlushAsync();
    }

    internal static async Task<byte[]> CaptureAsync(Window window, string name)
    {
        var frame = ReadFrame(window);
        await SaveAsync(frame, Path.Combine(AppContext.BaseDirectory, name));
        return frame.Pixels;
    }
}
