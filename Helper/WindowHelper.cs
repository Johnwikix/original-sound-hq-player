using System;
using System.Runtime.InteropServices;
using System.Text;

namespace WinUIMusicPlayer.Helper
{
    public static class WindowHelper
    {
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public const int SW_RESTORE = 9;

        public const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsWindow(IntPtr hWnd);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOPMOST = 0x00000008;
        private const int SW_SHOWNOACTIVATE = 4;
        private static readonly IntPtr HWND_TOPMOST = new(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint LWA_ALPHA = 0x00000002;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        /// <summary>
        /// 为覆盖层窗口一次性设置 WS_EX_LAYERED 与不透明属性（幂等，已分层则跳过）。
        /// 穿透开关 <see cref="SetClickThrough"/> 只切 WS_EX_TRANSPARENT，LAYERED 必须常驻：
        /// 运行期增删 LAYERED 会在前后台切换时与 DWM 分层合成竞态，偶发整窗隐身。
        /// </summary>
        public static void EnsureLayered(IntPtr hWnd)
        {
            int exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_LAYERED) != 0) return;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, (IntPtr)(exStyle | WS_EX_LAYERED));
            SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
        }

        /// <summary>
        /// 整窗点击穿透开关（只切 WS_EX_TRANSPARENT，需配合 <see cref="EnsureLayered"/> 的常驻 LAYERED），
        /// 供置顶覆盖层窗口（如桌面歌词悬浮窗）使用。
        /// </summary>
        public static void SetClickThrough(IntPtr hWnd, bool enable)
        {
            int exStyle = (int)GetWindowLongPtr(hWnd, GWL_EXSTYLE);
            exStyle = enable
                ? exStyle | WS_EX_TRANSPARENT
                : exStyle & ~WS_EX_TRANSPARENT;
            SetWindowLongPtr(hWnd, GWL_EXSTYLE, exStyle);
        }

        /// <summary>覆盖层自愈：不抢焦点地恢复显示并重申置顶。锁定态歌词窗不进任务栏/Alt-Tab，
        /// 一旦被前后台切换偶发置为不可见/最小化即无恢复入口（表现为歌词"消失"）。</summary>
        public static void RestoreOverlay(IntPtr hWnd)
        {
            ShowWindow(hWnd, SW_SHOWNOACTIVATE);
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

        /// <summary>窗口不在置顶层时幂等重申置顶（不动位置/尺寸/焦点）。</summary>
        public static void EnsureTopmost(IntPtr hWnd)
        {
            if (((int)GetWindowLongPtr(hWnd, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0) return;
            SetWindowPos(hWnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }

    }
}
