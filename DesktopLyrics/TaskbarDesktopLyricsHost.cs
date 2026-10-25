using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// 任务栏宿主。参考 AF-Media-Bar 的公开窗口托管方式，把歌词窗口作为任务栏子窗口挂载，
/// 不注入 Explorer，也不携带频谱或系统指标；媒体信息和播放控件仍由歌词窗口本身渲染。
/// 默认边界按固定媒体列和设置中的歌词列宽计算，手动解锁后的边界单独保存。
/// </summary>
internal sealed class TaskbarDesktopLyricsHost : IDesktopLyricsHost, IDesktopLyricsBoundsHost
{
    // The taskbar media strip is the compact cover + metadata + spectrum row in
    // DesktopLyricsWindow.xaml. Keep the host geometry in the same unit as the
    // visual tree so lyrics do not jump when the mode is first attached.
    private const int MediaPanelWidth = 280;
    private const int MediaPanelGap = 8;
    private const int MinimumLyricsWidth = 240;
    private const int MaximumLyricsWidth = 2400;

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = unchecked((int)0x80000000);
    private const long WS_CAPTION = 0x00C00000L;
    private const long WS_THICKFRAME = 0x00040000L;
    private const long WS_BORDER = 0x00800000L;
    private const long WS_VISIBLE = 0x10000000L;

    private const long WS_EX_APPWINDOW = 0x00040000L;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int SW_HIDE = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    private IntPtr _lyricsHwnd;
    private IntPtr _taskbarHwnd;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _disposed;
    private bool _visible = true;
    private readonly SaveDesktopLyricsState _savedBounds;
    private readonly Func<bool> _isLocked;
    private readonly Func<int> _getLyricsWidth;
    private TaskbarWindowBounds _currentBounds;
    private bool _hasCurrentBounds;

    public TaskbarDesktopLyricsHost(SaveDesktopLyricsState savedBounds, Func<bool> isLocked, Func<int> getLyricsWidth)
    {
        _savedBounds = savedBounds;
        _isLocked = isLocked;
        _getLyricsWidth = getLyricsWidth;
    }

    public DesktopLyricsMode Mode => DesktopLyricsMode.Taskbar;

    public void Attach(IntPtr hwnd, DispatcherQueue dispatcherQueue)
    {
        if (_disposed) return;
        _lyricsHwnd = hwnd;
        _refreshTimer ??= dispatcherQueue.CreateTimer();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(1000);
        _refreshTimer.Tick -= OnRefreshTimerTick;
        _refreshTimer.Tick += OnRefreshTimerTick;
        _refreshTimer.Start();
        Refresh();
    }

    public void Refresh()
    {
        if (_disposed || !_visible || _lyricsHwnd == IntPtr.Zero || !WindowHelper.IsWindow(_lyricsHwnd)) return;

        try
        {
            IntPtr taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !WindowHelper.IsWindow(taskbar)) return;
            // Explorer may hide/recreate child windows while rebuilding the taskbar.
            // Reassert visibility independently of geometry so a cached layout cannot
            // prevent recovery after that transition.
            ShowWindow(_lyricsHwnd, SW_SHOWNOACTIVATE);

            if (!GetClientRect(taskbar, out RECT client)) return;
            int clientWidth = Math.Max(1, client.Right - client.Left);
            int clientHeight = Math.Max(1, client.Bottom - client.Top);
            bool horizontal = clientWidth >= clientHeight;

            if (_taskbarHwnd != taskbar)
            {
                AttachToTaskbar(taskbar);
                _taskbarHwnd = taskbar;
                _hasCurrentBounds = false;
            }

            TaskbarWindowBounds desired = GetDesiredBounds(clientWidth, clientHeight, horizontal);
            if (!_hasCurrentBounds || desired != _currentBounds)
            {
                ApplyBounds(desired);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[TaskbarDesktopLyricsHost] refresh failed: {ex}");
        }
    }

    public void SetVisible(bool visible)
    {
        if (_disposed || _visible == visible) return;
        _visible = visible;
        if (!visible)
        {
            if (_lyricsHwnd != IntPtr.Zero && WindowHelper.IsWindow(_lyricsHwnd))
                ShowWindow(_lyricsHwnd, SW_HIDE);
            return;
        }
        if (_lyricsHwnd != IntPtr.Zero && WindowHelper.IsWindow(_lyricsHwnd))
            ShowWindow(_lyricsHwnd, SW_SHOWNOACTIVATE);
        Refresh();
    }

    public bool TryGetBounds(out TaskbarWindowBounds bounds)
    {
        bounds = _currentBounds;
        return _hasCurrentBounds;
    }

    public void SetUserBounds(TaskbarWindowBounds bounds)
    {
        if (_disposed || _isLocked() || _taskbarHwnd == IntPtr.Zero) return;
        if (!GetClientRect(_taskbarHwnd, out RECT client)) return;
        int clientWidth = Math.Max(1, client.Right - client.Left);
        int clientHeight = Math.Max(1, client.Bottom - client.Top);
        TaskbarWindowBounds clamped = ClampBounds(bounds, clientWidth, clientHeight, clientWidth >= clientHeight);
        ApplyBounds(clamped);
        SaveBounds(clamped);
    }

    public void ResetUserBounds()
    {
        if (_disposed) return;
        _savedBounds.TaskbarHasBounds = false;
        _hasCurrentBounds = false;
        Refresh();
    }

    public void SetLyricsWidth(int width)
    {
        if (_disposed) return;

        width = Math.Clamp(width, MinimumLyricsWidth, MaximumLyricsWidth);
        // 没有用户自定义边界时，重新计算默认居中布局，不把设置变化误记成拖动。
        if (!_savedBounds.TaskbarHasBounds)
        {
            _hasCurrentBounds = false;
            Refresh();
            return;
        }

        if (_taskbarHwnd == IntPtr.Zero || !GetClientRect(_taskbarHwnd, out RECT client))
        {
            _hasCurrentBounds = false;
            Refresh();
            return;
        }

        int clientWidth = Math.Max(1, client.Right - client.Left);
        int clientHeight = Math.Max(1, client.Bottom - client.Top);
        bool horizontal = clientWidth >= clientHeight;
        if (!horizontal)
        {
            Refresh();
            return;
        }

        if (!_hasCurrentBounds)
            Refresh();
        if (!_hasCurrentBounds) return;

        int totalWidth = ScaleLayoutLength(MediaPanelWidth + MediaPanelGap + width);
        TaskbarWindowBounds resized = ClampBounds(
            new TaskbarWindowBounds(_currentBounds.X, _currentBounds.Y, totalWidth, _currentBounds.Height),
            clientWidth, clientHeight, horizontal);
        ApplyBounds(resized);
        SaveBounds(resized);
    }

    private TaskbarWindowBounds GetDesiredBounds(int clientWidth, int clientHeight, bool horizontal)
    {
        if (_savedBounds.TaskbarHasBounds)
        {
            return ClampBounds(
                new TaskbarWindowBounds(_savedBounds.TaskbarX, _savedBounds.TaskbarY,
                    _savedBounds.TaskbarWidth, _savedBounds.TaskbarHeight),
                clientWidth, clientHeight, horizontal);
        }

        // 默认只占任务栏中段；用户解锁后可通过拖动和边缘缩放改变此布局。
        int lyricsWidth = ScaleLayoutLength(Math.Clamp(_getLyricsWidth(), MinimumLyricsWidth, MaximumLyricsWidth));
        int width = horizontal
            ? Math.Min(clientWidth, ScaleLayoutLength(MediaPanelWidth + MediaPanelGap) + lyricsWidth)
            : clientWidth;
        int height = horizontal ? clientHeight : Math.Min(clientHeight, 260);
        int x = horizontal ? Math.Max(0, (clientWidth - width) / 2) : 0;
        int y = horizontal ? 0 : Math.Max(0, (clientHeight - height) / 2);
        return new TaskbarWindowBounds(x, y, width, height);
    }

    private TaskbarWindowBounds ClampBounds(TaskbarWindowBounds bounds, int clientWidth, int clientHeight, bool horizontal)
    {
        int requestedMinimumWidth = horizontal
            ? ScaleLayoutLength(MediaPanelWidth + MediaPanelGap + MinimumLyricsWidth)
            : 240;
        int minWidth = Math.Min(clientWidth, requestedMinimumWidth);
        int minHeight = Math.Min(clientHeight, 32);
        int width = Math.Clamp(bounds.Width, minWidth, clientWidth);
        int height = Math.Clamp(bounds.Height, minHeight, clientHeight);
        int x = Math.Clamp(bounds.X, 0, Math.Max(0, clientWidth - width));
        int y = Math.Clamp(bounds.Y, 0, Math.Max(0, clientHeight - height));
        return new TaskbarWindowBounds(x, y, width, height);
    }

    private int ScaleLayoutLength(int dipLength)
    {
        uint dpi = _taskbarHwnd == IntPtr.Zero ? 96u : GetDpiForWindow(_taskbarHwnd);
        if (dpi == 0) dpi = 96;
        return Math.Max(1, (int)Math.Round(dipLength * dpi / 96d));
    }

    private void SaveBounds(TaskbarWindowBounds bounds)
    {
        _savedBounds.TaskbarHasBounds = true;
        _savedBounds.TaskbarX = bounds.X;
        _savedBounds.TaskbarY = bounds.Y;
        _savedBounds.TaskbarWidth = bounds.Width;
        _savedBounds.TaskbarHeight = bounds.Height;
    }

    private void ApplyBounds(TaskbarWindowBounds bounds)
    {
        if (_lyricsHwnd == IntPtr.Zero || !WindowHelper.IsWindow(_lyricsHwnd)) return;
        if (SetWindowPos(_lyricsHwnd, HWND_TOP, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED))
        {
            _currentBounds = bounds;
            _hasCurrentBounds = true;
        }
    }

    private void AttachToTaskbar(IntPtr taskbar)
    {
        long style = GetWindowLongPtr(_lyricsHwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(_lyricsHwnd, GWL_STYLE, new IntPtr(style));

        long exStyle = GetWindowLongPtr(_lyricsHwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT);
        // 任务栏模式包含播放按钮，不能使用 WS_EX_TRANSPARENT 或 WS_EX_NOACTIVATE，
        // 否则鼠标事件会直接落到 Explorer。WS_CHILD 已经限定了窗口的宿主和激活范围。
        exStyle |= WS_EX_TOOLWINDOW;
        SetWindowLongPtr(_lyricsHwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        SetParent(_lyricsHwnd, taskbar);
        if (GetParent(_lyricsHwnd) != taskbar)
            throw new InvalidOperationException("无法将桌面歌词挂载到任务栏。");
    }

    private void OnRefreshTimerTick(DispatcherQueueTimer sender, object args) => Refresh();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _refreshTimer?.Stop();
        _refreshTimer = null;
        if (_lyricsHwnd != IntPtr.Zero && WindowHelper.IsWindow(_lyricsHwnd))
        {
            try
            {
                SetParent(_lyricsHwnd, IntPtr.Zero);
                ShowWindow(_lyricsHwnd, SW_HIDE);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[TaskbarDesktopLyricsHost] detach failed: {ex}");
            }
        }
        _taskbarHwnd = IntPtr.Zero;
        _lyricsHwnd = IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
