using Microsoft.UI.Windowing;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Graphics;

namespace WinUIMusicPlayer.Helper
{
    public static class WindowSizeHelper
    {
        private static readonly Dictionary<IntPtr, (int minWidth, int minHeight, IntPtr oldWndProc, WndProcDelegate newWndProc)> _windowData
            = new Dictionary<IntPtr, (int, int, IntPtr, WndProcDelegate)>();

        //public static void SetMinimumSize(IntPtr hwnd, Window window, int minWidth, int minHeight)
        //{
        //    //var hwnd = WindowNative.GetWindowHandle(window);
        //    uint dpi = GetDpiForWindow(hwnd);
        //    double scaleFactor = dpi / 96.0;
        //    AppData.AppDpiScale = scaleFactor;
        //    minWidth = (int)(minWidth * scaleFactor);
        //    minHeight = (int)(minHeight * scaleFactor);
        //    // 创建新的窗口过程
        //    var newWndProc = new WndProcDelegate((hwnd, msg, wParam, lParam) =>
        //        WndProc(hwnd, msg, wParam, lParam));

        //    // 保存原始窗口过程
        //    var oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newWndProc);

        //    // 存储窗口数据
        //    _windowData[hwnd] = (minWidth, minHeight, oldWndProc, newWndProc);

        //    // 窗口关闭时清理
        //    window.Closed += (sender, args) =>
        //    {
        //        if (_windowData.ContainsKey(hwnd))
        //        {
        //            _windowData.Remove(hwnd);
        //        }
        //    };
        //}

        private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (!_windowData.TryGetValue(hwnd, out var data))
            {
                return DefWindowProc(hwnd, msg, wParam, lParam);
            }

            var (minWidth, minHeight, oldWndProc, _) = data;

            switch (msg)
            {
                case WM_SIZING:
                    var rect = Marshal.PtrToStructure<RECT>(lParam);

                    var width = rect.Right - rect.Left;
                    var height = rect.Bottom - rect.Top;
                    var edge = wParam.ToInt32();

                    // 限制最小宽度
                    if (width < minWidth)
                    {
                        var diff = minWidth - width;
                        if (edge == WMSZ_LEFT || edge == WMSZ_TOPLEFT || edge == WMSZ_BOTTOMLEFT)
                        {
                            rect.Left -= diff;
                        }
                        else
                        {
                            rect.Right += diff;
                        }
                    }

                    // 限制最小高度
                    if (height < minHeight)
                    {
                        var diff = minHeight - height;
                        if (edge == WMSZ_TOP || edge == WMSZ_TOPLEFT || edge == WMSZ_TOPRIGHT)
                        {
                            rect.Top -= diff;
                        }
                        else
                        {
                            rect.Bottom += diff;
                        }
                    }

                    Marshal.StructureToPtr(rect, lParam, true);
                    return new IntPtr(1);

                case WM_GETMINMAXINFO:
                    var minMaxInfo = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                    minMaxInfo.ptMinTrackSize.X = minWidth;
                    minMaxInfo.ptMinTrackSize.Y = minHeight;
                    Marshal.StructureToPtr(minMaxInfo, lParam, true);
                    return IntPtr.Zero;
            }

            return CallWindowProc(oldWndProc, hwnd, msg, wParam, lParam);
        }

        /// <summary>
        /// 跨显示器安全的移动+还原尺寸,替代 AppWindow.MoveAndResize。
        /// MoveAndResize 内部先 resize 后 move(microsoft-ui-xaml#10498),resize 会按移动前
        /// 所在显示器的 DPI 解释;且移动到不同 DPI 显示器时 WinUI 会按 WM_DPICHANGED 自动
        /// 缩放尺寸(WinAppSDK 1.1.2 起)。先 Move 让 DPI 切换完成,再 Resize 施加精确物理
        /// 尺寸,保证还原值与保存值逐像素一致。
        /// </summary>
        public static void MoveAndResizeExact(AppWindow appWindow, int x, int y, int width, int height)
        {
            appWindow.Move(new PointInt32(x, y));
            appWindow.Resize(new SizeInt32(width, height));
            // 兜底:若 DPI 切换的自动缩放异步落在 Resize 之后,重放一次精确尺寸
            if (appWindow.Size.Width != width || appWindow.Size.Height != height)
            {
                appWindow.Resize(new SizeInt32(width, height));
            }
        }

        public static void ResizeWindowAndCenterInMainWindow(IntPtr hwnd, int height, int width, AppWindow mainWindow, AppWindow appWindow)
        {
            PointInt32 mainWindowPosition = mainWindow.Position;
            int mainWindowWidth = mainWindow.Size.Width;
            int mainWindowHeight = mainWindow.Size.Height;

            // 新窗口此刻仍在创建时的默认显示器上,直接换算会取错 DPI。
            // 先按当前 DPI 粗算落点并 Move 到主窗口所在显示器,让窗口 DPI 切换完成,
            // 之后 GetDpiForWindow 返回的才是目标显示器的 DPI。
            double initialScale = GetDpiForWindow(hwnd) / 96.0;
            int estimatedWidth = (int)(width * initialScale);
            int estimatedHeight = (int)(height * initialScale);
            appWindow.Move(new PointInt32(
                mainWindowPosition.X + (mainWindowWidth - estimatedWidth) / 2,
                mainWindowPosition.Y + (mainWindowHeight - estimatedHeight) / 2));

            double scaleFactor = GetDpiForWindow(hwnd) / 96.0;
            int adjustedWidth = (int)(width * scaleFactor);
            int adjustedHeight = (int)(height * scaleFactor);
            int centerX = mainWindowPosition.X + (mainWindowWidth - adjustedWidth) / 2;
            int centerY = mainWindowPosition.Y + (mainWindowHeight - adjustedHeight) / 2;
            MoveAndResizeExact(appWindow, centerX, centerY, adjustedWidth, adjustedHeight);
        }

        public static void ResizeWindowAndCenterInScreen(IntPtr hwnd, int height, int width, AppWindow appWindow)
        {
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            uint dpi = GetDpiForWindow(hwnd);
            double scaleFactor = dpi / 96.0;
            int adjustedWidth = Math.Min((int)(width * scaleFactor), displayArea.WorkArea.Width);
            int adjustedHeight = Math.Min((int)(height * scaleFactor), displayArea.WorkArea.Height);
            appWindow.Resize(new SizeInt32(adjustedWidth, adjustedHeight));
            var x = (displayArea.WorkArea.Width - appWindow.Size.Width) / 2 + displayArea.WorkArea.X;
            var y = (displayArea.WorkArea.Height - appWindow.Size.Height) / 2 + displayArea.WorkArea.Y;
            appWindow.Move(new PointInt32(x, y));
        }

        public static void CenterInScreen(AppWindow appWindow)
        {
            var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var windowSize = appWindow.Size;
            var x = (workArea.Width - windowSize.Width) / 2 + workArea.X;
            var y = (workArea.Height - windowSize.Height) / 2 + workArea.Y;
            appWindow.Move(new PointInt32(x, y));
        }

        public static float GetScaleFactor(IntPtr hwnd)
        {
            return GetDpiForWindow(hwnd) / 96.0f;
        }

        public static bool IsAppWindowMaximized(AppWindow appWindow)
        {
            return appWindow?.Presenter is OverlappedPresenter op
                && op.State == OverlappedPresenterState.Maximized;
        }

        public static bool IsBoundsOnScreen(int x, int y, int width, int height, int minVisible = 30)
        {
            if (width <= 0 || height <= 0) return false;

            var areas = DisplayArea.FindAll();
            for (int i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                var work = area.WorkArea;
                int left = Math.Max(x, work.X);
                int top = Math.Max(y, work.Y);
                int right = Math.Min(x + width, work.X + work.Width);
                int bottom = Math.Min(y + height, work.Y + work.Height);
                int visibleW = right - left;
                int visibleH = bottom - top;
                if (visibleW >= minVisible && visibleH >= minVisible)
                {
                    return true;
                }
            }
            return false;
        }

        // Win32 API 声明
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        private const int GWLP_WNDPROC = -4;
        private const uint WM_SIZING = 0x0214;
        private const uint WM_GETMINMAXINFO = 0x0024;

        private const int WMSZ_LEFT = 1;
        private const int WMSZ_RIGHT = 2;
        private const int WMSZ_TOP = 3;
        private const int WMSZ_TOPLEFT = 4;
        private const int WMSZ_TOPRIGHT = 5;
        private const int WMSZ_BOTTOM = 6;
        private const int WMSZ_BOTTOMLEFT = 7;
        private const int WMSZ_BOTTOMRIGHT = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X, Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }
    }
}
