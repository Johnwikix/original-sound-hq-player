using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinUIMusicPlayer.Helper
{
    public static class SingleInstanceHelper
    {
        private static Mutex _mutex = null;
        private const string MutexName = "WinUIMusicPlayer_SingleInstanceMutex";
        public const int WM_SHOWME = 0x8001;

        /// <summary>
        /// 检查应用程序是否已经在运行
        /// </summary>
        /// <returns>如果应用程序是首次运行则返回true，否则返回false</returns>
        public static bool CheckSingleInstance()
        {
            bool mutexCreated;
            _mutex = new Mutex(true, MutexName, out mutexCreated);

            // 如果互斥量已经存在，则表示应用已经在运行
            return mutexCreated;
        }

        /// <summary>
        /// 尝试激活已有的应用程序实例
        /// </summary>
        public static void ActivateExistingInstance()
        {
            try
            {
                // 使用更可靠的方法查找窗口 - 通过进程和窗口标题
                Process current = Process.GetCurrentProcess();
                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    if (process.Id != current.Id)
                    {
                        // 尝试枚举进程的所有窗口
                        bool windowFound = false;
                        EnumWindows((hWnd, lParam) =>
                        {
                            uint processId;
                            GetWindowThreadProcessId(hWnd, out processId);

                            if (processId == process.Id)
                            {
                                int length = GetWindowTextLength(hWnd);
                                if (length > 0)
                                {
                                    StringBuilder windowTitle = new StringBuilder(length + 1);
                                    GetWindowText(hWnd, windowTitle, windowTitle.Capacity);

                                    // 检查窗口标题，或者只要找到第一个可见窗口就激活
                                    if (IsWindowVisible(hWnd))
                                    {
                                        // 如果窗口最小化，则恢复它
                                        if (IsIconic(hWnd))
                                        {
                                            ShowWindow(hWnd, SW_RESTORE);
                                        }

                                        // 将窗口置于前台
                                        SetForegroundWindow(hWnd);
                                        return false; // 停止枚举
                                    }
                                }

                                // 发送自定义消息给窗口，通知它显示
                                SendMessage(hWnd, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                                windowFound = true;
                                return false; // 停止枚举
                            }

                            return true; // 继续枚举
                        }, IntPtr.Zero);

                        // 如果找到窗口，则退出函数
                        if (windowFound)
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"激活已有实例时出错: {ex.Message}");
            }
        }

        /// <summary>
        /// 释放互斥量资源
        /// </summary>
        public static void ReleaseMutex()
        {
            if (_mutex != null)
            {
                _mutex.ReleaseMutex();
                _mutex.Dispose();
                _mutex = null;
            }
        }

        #region Win32 API 声明
        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        #endregion
    }
}
