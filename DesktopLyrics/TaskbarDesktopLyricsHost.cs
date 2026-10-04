using Microsoft.UI.Dispatching;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.DesktopLyrics;

/// <summary>
/// 任务栏宿主。参考 AF-Media-Bar 的公开窗口托管方式，把歌词窗口作为任务栏子窗口挂载，
/// 不注入 Explorer，也不携带频谱、系统指标或媒体控制组件。
/// </summary>
internal sealed class TaskbarDesktopLyricsHost : IDesktopLyricsHost
{
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

    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    private IntPtr _lyricsHwnd;
    private IntPtr _taskbarHwnd;
    private DispatcherQueueTimer? _refreshTimer;
    private bool _disposed;
    private bool _visible = true;

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

            if (!GetClientRect(taskbar, out RECT client)) return;
            int clientWidth = Math.Max(1, client.Right - client.Left);
            int clientHeight = Math.Max(1, client.Bottom - client.Top);
            bool horizontal = clientWidth >= clientHeight;

            // 保持歌词条短而可读，避免覆盖整个任务栏。竖向任务栏则沿纵向居中。
            int width = horizontal ? Math.Min(clientWidth, 720) : clientWidth;
            int height = horizontal ? clientHeight : Math.Min(clientHeight, 260);
            int x = horizontal ? Math.Max(0, (clientWidth - width) / 2) : 0;
            int y = horizontal ? 0 : Math.Max(0, (clientHeight - height) / 2);

            if (_taskbarHwnd != taskbar)
            {
                AttachToTaskbar(taskbar);
                _taskbarHwnd = taskbar;
            }

            SetWindowPos(_lyricsHwnd, HWND_TOP, x, y, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
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
        Refresh();
    }

    private void AttachToTaskbar(IntPtr taskbar)
    {
        long style = GetWindowLongPtr(_lyricsHwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_POPUP | WS_CAPTION | WS_THICKFRAME | WS_BORDER);
        style |= WS_CHILD | WS_VISIBLE;
        SetWindowLongPtr(_lyricsHwnd, GWL_STYLE, new IntPtr(style));

        long exStyle = GetWindowLongPtr(_lyricsHwnd, GWL_EXSTYLE).ToInt64();
        exStyle &= ~(WS_EX_APPWINDOW | WS_EX_TOPMOST);
        exStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
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
