using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// 将桌面歌词窗口嵌入 Explorer 壁纸层。第一版使用主显示器，所有调用都在窗口 UI 线程执行。
/// </summary>
internal sealed class WallpaperDesktopLyricsHost : IDesktopLyricsHost
{
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const long WsChild = 0x40000000;
    private const long WsPopup = unchecked((int)0x80000000);
    private const long WindowChrome = 0x00CF0000;
    private const long WsExAppWindow = 0x00040000;
    private const long WsExToolWindow = 0x00000080;
    private const long WsExNoActivate = 0x08000000;
    private const long WsExTransparent = 0x00000020;
    private const long WsExTopmost = 0x00000008;
    private const long WsExNoRedirectionBitmap = 0x00200000;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;
    private const uint SwpShowWindow = 0x0040;
    private const int SwHide = 0;
    private const int SwShowNoActivate = 4;

    private readonly IntPtr _originalParent;
    private readonly long _originalStyle;
    private readonly long _originalExStyle;
    private readonly WindowRect _originalBounds;
    private readonly Action<bool>? _setRenderingSuspended;
    private IntPtr _lyricsHwnd;
    private IntPtr _desktop;
    private IntPtr _icons;
    private DispatcherQueueTimer? _refreshTimer;
    private DispatcherQueueTimer? _visibilityTimer;
    private WallpaperOcclusionDetector? _occlusionDetector;
    private bool _renderingSuspended;
    private bool _disposed;
    private bool _visible = true;

    public WallpaperDesktopLyricsHost(IntPtr hwnd, Action<bool>? setRenderingSuspended = null)
    {
        _lyricsHwnd = hwnd;
        _setRenderingSuspended = setRenderingSuspended;
        _originalParent = GetParent(hwnd);
        _originalStyle = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        _originalExStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        if (!GetWindowRect(hwnd, out _originalBounds))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public DesktopLyricsMode Mode => DesktopLyricsMode.Wallpaper;

    public void Attach(IntPtr hwnd, DispatcherQueue dispatcherQueue)
    {
        if (_disposed) return;
        _lyricsHwnd = hwnd;
        _refreshTimer ??= dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(2000);
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        _occlusionDetector ??= new WallpaperOcclusionDetector(_lyricsHwnd);
        _visibilityTimer ??= dispatcherQueue.CreateTimer();
        _visibilityTimer.Interval = TimeSpan.FromMilliseconds(500);
        _visibilityTimer.Tick -= OnVisibilityTimerTick;
        _visibilityTimer.Tick += OnVisibilityTimerTick;
        _visibilityTimer.Start();
        Refresh();
        UpdateRenderingSuspended();
    }

    public void Refresh()
    {
        if (_disposed || !_visible || _lyricsHwnd == IntPtr.Zero || !IsWindow(_lyricsHwnd)) return;

        try
        {
            if (!IsWindow(_desktop) || GetParent(_lyricsHwnd) != _desktop ||
                (_icons != IntPtr.Zero && !IsWindow(_icons)))
            {
                AttachToDesktop();
                return;
            }

            FitPrimaryMonitor(false);
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "刷新桌面歌词壁纸宿主失败");
        }
    }

    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (_lyricsHwnd == IntPtr.Zero || !IsWindow(_lyricsHwnd)) return;

        ShowWindow(_lyricsHwnd, visible ? SwShowNoActivate : SwHide);
        if (visible)
        {
            Refresh();
            UpdateRenderingSuspended();
        }
        else
        {
            SetRenderingSuspended(false);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Stop();
        _refreshTimer = null;
        _visibilityTimer?.Stop();
        _visibilityTimer = null;
        SetRenderingSuspended(false);

        if (_lyricsHwnd == IntPtr.Zero || !IsWindow(_lyricsHwnd)) return;
        try
        {
            // 还原父窗口后再清掉 WS_CHILD，避免 SetParent(NULL) 后留下子窗口样式。
            ChangeParent(_originalParent);
            SetStyle(GwlStyle, _originalStyle);
            SetStyle(GwlExStyle, _originalExStyle);
            SetWindowPos(_lyricsHwnd,
                (_originalExStyle & WsExTopmost) != 0 ? new IntPtr(-1) : new IntPtr(-2),
                _originalBounds.Left,
                _originalBounds.Top,
                _originalBounds.Right - _originalBounds.Left,
                _originalBounds.Bottom - _originalBounds.Top,
                SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Warning(ex, "还原桌面歌词壁纸宿主失败");
        }
        finally
        {
            _desktop = IntPtr.Zero;
            _icons = IntPtr.Zero;
            _lyricsHwnd = IntPtr.Zero;
        }
    }

    private void AttachToDesktop()
    {
        (IntPtr desktop, IntPtr icons) = FindDesktop();
        if (desktop == IntPtr.Zero)
            throw new InvalidOperationException("Explorer 桌面壁纸层不可用。");

        _desktop = desktop;
        _icons = icons;
        // 先移除置顶，再设置 WS_CHILD，避免锁定态的置顶策略泄漏到桌面层。
        Check(SetWindowPos(_lyricsHwnd, new IntPtr(-2), 0, 0, 0, 0, 0x13));
        SetStyle(GwlStyle, (_originalStyle & ~(WsPopup | WindowChrome)) | WsChild);
        SetStyle(GwlExStyle, (_originalExStyle & ~(WsExAppWindow | WsExTopmost)) |
            WsExToolWindow | WsExNoActivate | WsExTransparent);
        WindowHelper.EnsureLayered(_lyricsHwnd);
        ChangeParent(_desktop);
        FitPrimaryMonitor(true);
    }

    private void FitPrimaryMonitor(bool force)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        Check(GetMonitorInfo(MonitorFromPoint(default, 1), ref info));
        WindowRect bounds = info.Monitor;
        Check(GetWindowRect(_lyricsHwnd, out WindowRect current));
        bool correctLayer = _icons == IntPtr.Zero || GetWindow(_lyricsHwnd, 3) == _icons;
        if (!force && bounds.Equals(current) && correctLayer) return;

        var origin = new WindowHelper.POINT { X = bounds.Left, Y = bounds.Top };
        Check(ScreenToClient(_desktop, ref origin));
        Check(SetWindowPos(_lyricsHwnd, _icons, origin.X, origin.Y,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top,
            SwpNoActivate | SwpFrameChanged | SwpShowWindow));
    }

    private void OnRefreshTimerTick(DispatcherQueueTimer sender, object args) => Refresh();

    private void OnVisibilityTimerTick(DispatcherQueueTimer sender, object args) => UpdateRenderingSuspended();

    private void UpdateRenderingSuspended()
    {
        if (_disposed || !_visible || _lyricsHwnd == IntPtr.Zero || !IsWindow(_lyricsHwnd))
        {
            SetRenderingSuspended(false);
            return;
        }

        bool covered = false;
        try
        {
            covered = _occlusionDetector?.IsFullyCovered() == true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Logger.Debug(ex, "检测桌面歌词遮挡状态失败，继续渲染");
        }
        SetRenderingSuspended(covered);
    }

    private void SetRenderingSuspended(bool suspended)
    {
        if (_renderingSuspended == suspended) return;
        _renderingSuspended = suspended;
        _setRenderingSuspended?.Invoke(suspended);
    }

    private static (IntPtr Host, IntPtr Icons) FindDesktop()
    {
        IntPtr progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return default;

        if (SendMessageTimeout(progman, 0x052C, new IntPtr(0xD), new IntPtr(1),
                0x2, 1000, out _) == IntPtr.Zero)
            return default;

        IntPtr icons = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (icons != IntPtr.Zero &&
            (GetWindowLongPtr(progman, GwlExStyle).ToInt64() & WsExNoRedirectionBitmap) != 0)
            return (progman, icons);

        IntPtr host = IntPtr.Zero;
        EnumWindows((window, _) =>
        {
            if (FindWindowEx(window, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
                return true;
            IntPtr candidate = FindWindowEx(IntPtr.Zero, window, "WorkerW", null);
            if (candidate == IntPtr.Zero ||
                FindWindowEx(candidate, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                return true;
            host = candidate;
            return false;
        }, IntPtr.Zero);
        return (host, IntPtr.Zero);
    }

    private void ChangeParent(IntPtr parent)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetParent(_lyricsHwnd, parent);
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
        if (parent != IntPtr.Zero && GetParent(_lyricsHwnd) != parent)
            throw new InvalidOperationException("无法将桌面歌词挂载到壁纸层。");
    }

    private static void SetStyle(IntPtr hwnd, int index, long style)
    {
        Marshal.SetLastPInvokeError(0);
        IntPtr previous = SetWindowLongPtr(hwnd, index, new IntPtr(style));
        int error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
    }

    private void SetStyle(int index, long style) => SetStyle(_lyricsHwnd, index, style);

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect : IEquatable<WindowRect>
    {
        public int Left, Top, Right, Bottom;
        public readonly bool Equals(WindowRect other) => Left == other.Left && Top == other.Top &&
            Right == other.Right && Bottom == other.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public WindowRect Monitor, Work;
        public uint Flags;
    }

    private delegate bool EnumWindowProc(IntPtr hwnd, IntPtr parameter);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam,
        IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint command);
    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ScreenToClient(IntPtr hwnd, ref WindowHelper.POINT point);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(WindowHelper.POINT point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);
}
