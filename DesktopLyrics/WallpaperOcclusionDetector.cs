using System;
using System.Runtime.InteropServices;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>Full-screen or maximized work-area coverage check in physical screen coordinates.
/// Used only in wallpaper mode, on the UI thread; it does not capture screen pixels.</summary>
internal sealed unsafe class WallpaperOcclusionDetector
{
    private const int GwlExStyle = -20;
    private const long WsExLayered = 0x80000, WsExTransparent = 0x20;
    private readonly IntPtr _wallpaperWindow;
    private readonly EnumWindowProc _visitWindow;
    private WindowRect _wallpaperBounds;
    private IntPtr _wallpaperMonitor;
    private MonitorInfo _monitorInfo;
    private bool _covered;

    public WallpaperOcclusionDetector(IntPtr wallpaperWindow)
    {
        _wallpaperWindow = wallpaperWindow;
        _visitWindow = VisitWindow;
    }

    public bool IsFullyCovered()
    {
        if (!GetWindowRect(_wallpaperWindow, out _wallpaperBounds) ||
            _wallpaperBounds.Right <= _wallpaperBounds.Left || _wallpaperBounds.Bottom <= _wallpaperBounds.Top)
            return false;

        // Win+D can bring the desktop above windows that still report WS_VISIBLE.
        if (IsDesktopClass(GetForegroundWindow())) return false;
        _wallpaperMonitor = MonitorFromWindow(_wallpaperWindow, 0 /* MONITOR_DEFAULTTONULL */);
        _monitorInfo = new MonitorInfo { Size = sizeof(MonitorInfo) };
        if (_wallpaperMonitor == IntPtr.Zero || !GetMonitorInfo(_wallpaperMonitor, ref _monitorInfo))
            _monitorInfo = default;
        _covered = false;
        EnumWindows(_visitWindow, IntPtr.Zero);
        return _covered;
    }

    private bool VisitWindow(IntPtr window, IntPtr parameter)
    {
        if (window == _wallpaperWindow || !IsWindowVisible(window) || IsIconic(window)) return true;
        if ((GetWindowLongPtr(window, GwlExStyle).ToInt64() & (WsExLayered | WsExTransparent)) != 0) return true;
        // Includes windows on another virtual desktop and hidden UWP hosts.
        if (DwmGetWindowAttribute(window, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) != 0 || cloaked != 0)
            return true;
        // DWM excludes invisible resize borders, unlike GetWindowRect. If the
        // visible bounds cannot be read, keep rendering rather than guess.
        if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out WindowRect bounds,
                sizeof(WindowRect)) != 0) return true;
        bool maximizedOnWallpaperMonitor = IsZoomed(window) && _wallpaperMonitor != IntPtr.Zero &&
            MonitorFromWindow(window, 0 /* MONITOR_DEFAULTTONULL */) == _wallpaperMonitor;
        WindowRect coverageBounds = GetCoverageBounds(_wallpaperBounds, _monitorInfo.Monitor,
            _monitorInfo.Work, maximizedOnWallpaperMonitor);
        if (!Covers(bounds, coverageBounds)) return true;
        // .NET 10: 栈上复用类名缓冲，轮询不创建 StringBuilder 或临时字符串。
        char* className = stackalloc char[256];
        int length = GetClassName(window, className, 256);
        if (length == 0) return true;
        var name = new ReadOnlySpan<char>(className, length);
        if (IsDesktopClass(name) || name.SequenceEqual("Shell_TrayWnd") ||
            name.SequenceEqual("Shell_SecondaryTrayWnd")) return true;
        // A shaped window may have holes even when its bounding box is full-screen.
        int regionType = GetWindowRgnBox(window, out WindowRect region);
        if (regionType is 1 or 3 /* NULLREGION / COMPLEXREGION */) return true;
        if (regionType == 2 /* SIMPLEREGION */)
        {
            if (!GetWindowRect(window, out WindowRect windowBounds)) return true;
            region.Left += windowBounds.Left;
            region.Right += windowBounds.Left;
            region.Top += windowBounds.Top;
            region.Bottom += windowBounds.Top;
            if (!Covers(region, coverageBounds)) return true;
        }
        _covered = true;
        return false;
    }

    private static bool IsDesktopClass(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        char* className = stackalloc char[256];
        int length = GetClassName(window, className, 256);
        return IsDesktopClass(new ReadOnlySpan<char>(className, length));
    }

    private static bool IsDesktopClass(ReadOnlySpan<char> name) =>
        name.SequenceEqual("Progman") || name.SequenceEqual("WorkerW") || name.SequenceEqual("SHELLDLL_DefView");

    /// <summary>Only a maximized window on the wallpaper monitor may exclude its reserved work-area edges.</summary>
    internal static WindowRect GetCoverageBounds(WindowRect wallpaper, WindowRect monitor, WindowRect work,
        bool maximizedOnWallpaperMonitor)
    {
        // 壁纸包含任务栏区域，最大化窗口只需覆盖工作区；普通窗口仍须完整遮挡壁纸。
        // 显示器信息失败、过期或壁纸跨屏时保留完整边界，避免因空交集误暂停。
        if (!maximizedOnWallpaperMonitor || !Covers(monitor, wallpaper) || !Covers(monitor, work))
            return wallpaper;
        var intersection = new WindowRect
        {
            Left = Math.Max(wallpaper.Left, work.Left),
            Top = Math.Max(wallpaper.Top, work.Top),
            Right = Math.Min(wallpaper.Right, work.Right),
            Bottom = Math.Min(wallpaper.Bottom, work.Bottom)
        };
        return intersection.Right > intersection.Left && intersection.Bottom > intersection.Top
            ? intersection : wallpaper;
    }

    internal static bool Covers(WindowRect window, WindowRect wallpaper) =>
        wallpaper.Right > wallpaper.Left && wallpaper.Bottom > wallpaper.Top &&
        window.Left <= wallpaper.Left && window.Top <= wallpaper.Top &&
        window.Right >= wallpaper.Right && window.Bottom >= wallpaper.Bottom;

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowRect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor, Work;
        public uint Flags;
    }

    private delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", ExactSpelling = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out WindowRect bounds);
    [DllImport("user32.dll")]
    private static extern int GetWindowRgnBox(IntPtr window, out WindowRect bounds);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", ExactSpelling = true)]
    private static extern int GetClassName(IntPtr window, char* name, int capacity);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out WindowRect value, int size);
}

