using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Helper;

namespace WinUIMusicPlayer.Handler
{
    public class WindowMessageHandler : IDisposable
    {
        private readonly IntPtr _hwnd;
        private readonly WindowProc _wndProc;
        private readonly WindowProc _prevWndProc;
        private bool _disposed = false;

        public event EventHandler<WindowMessageEventArgs> MessageReceived;

        public WindowMessageHandler(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _wndProc = WndProc;
            // 替换窗口过程
            _prevWndProc = SubclassWindow(_hwnd, _wndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 处理自定义消息
            if (msg == SingleInstanceHelper.WM_SHOWME)
            {
                MessageReceived?.Invoke(this, new WindowMessageEventArgs(msg, wParam, lParam));
            }

            // 调用原始窗口过程
            return CallWindowProc(_prevWndProc, hwnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                // 恢复原始窗口过程
                SubclassWindow(_hwnd, _prevWndProc);
                _disposed = true;
            }
        }

        // Win32 API 定义
        private delegate IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(WindowProc lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private const int GWLP_WNDPROC = -4;

        private WindowProc SubclassWindow(IntPtr hWnd, WindowProc newProc)
        {
            IntPtr prevWndProc = SetWindowLongPtr(hWnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(newProc));
            return Marshal.GetDelegateForFunctionPointer<WindowProc>(prevWndProc);
        }
    }
}
