using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WinRT.Interop;

namespace WinUIMusicPlayer.Helper
{
    public static class WindowSizeHelper
    {
        private static readonly Dictionary<IntPtr, (int minWidth, int minHeight, IntPtr oldWndProc, WndProcDelegate newWndProc)> _windowData
            = new Dictionary<IntPtr, (int, int, IntPtr, WndProcDelegate)>();

        public static void SetMinimumSize(IntPtr hwnd,Window window, int minWidth, int minHeight)
        {
            //var hwnd = WindowNative.GetWindowHandle(window);

            // 创建新的窗口过程
            var newWndProc = new WndProcDelegate((hwnd, msg, wParam, lParam) =>
                WndProc(hwnd, msg, wParam, lParam));

            // 保存原始窗口过程
            var oldWndProc = SetWindowLongPtr(hwnd, GWLP_WNDPROC, newWndProc);

            // 存储窗口数据
            _windowData[hwnd] = (minWidth, minHeight, oldWndProc, newWndProc);

            // 窗口关闭时清理
            window.Closed += (sender, args) =>
            {
                if (_windowData.ContainsKey(hwnd))
                {
                    _windowData.Remove(hwnd);
                }
            };
        }

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

        // Win32 API 声明
        private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

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
