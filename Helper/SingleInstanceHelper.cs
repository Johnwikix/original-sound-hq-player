using System;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace WinUIMusicPlayer.Helper
{
    public static class SingleInstanceHelper
    {
        private static Mutex _mutex = null;
        private const string MutexName = "WinUIMusicPlayer_SingleInstanceMutex";
        public const int WM_SHOWME = 0x8001;
        private const string WindowIdentifier = "WinUIMusicPlayer_MainWindow";

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
                // 查找我们应用程序的所有进程实例
                Process current = Process.GetCurrentProcess();
                foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                {
                    // 跳过当前进程
                    if (process.Id == current.Id)
                        continue;

                    // 查找其他进程实例的主窗口
                    bool windowFound = false;
                    WindowHelper.EnumWindows((hWnd, lParam) =>
                    {
                        uint processId;
                        WindowHelper.GetWindowThreadProcessId(hWnd, out processId);

                        // 检查窗口是否属于我们要查找的进程
                        if (processId == process.Id)
                        {
                            // 发送自定义消息到窗口，通知它显示
                            // 这个消息将被原来的实例捕获并处理
                            WindowHelper.SendMessage(hWnd, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                            windowFound = true;
                            return false; // 停止枚举
                        }
                        return true; // 继续枚举
                    }, IntPtr.Zero);

                    if (windowFound)
                        break;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"激活已有实例时出错: {ex.Message}");
            }
        }
        //public static void ActivateExistingInstance()
        //{
        //    try
        //    {
        //        // 使用更可靠的方法查找窗口 - 通过进程和窗口标题
        //        Process current = Process.GetCurrentProcess();
        //        foreach (Process process in Process.GetProcessesByName(current.ProcessName))
        //        {
        //            if (process.Id != current.Id)
        //            {
        //                // 尝试枚举进程的所有窗口
        //                bool windowFound = false;
        //                WindowHelper.EnumWindows((hWnd, lParam) =>
        //                {
        //                    uint processId;
        //                    WindowHelper.GetWindowThreadProcessId(hWnd, out processId);

        //                    if (processId == process.Id)
        //                    {
        //                        int length = WindowHelper.GetWindowTextLength(hWnd);
        //                        if (length > 0)
        //                        {
        //                            StringBuilder windowTitle = new StringBuilder(length + 1);
        //                            WindowHelper.GetWindowText(hWnd, windowTitle, windowTitle.Capacity);

        //                            // 检查窗口标题，或者只要找到第一个可见窗口就激活
        //                            if (WindowHelper.IsWindowVisible(hWnd))
        //                            {
        //                                // 如果窗口最小化，则恢复它
        //                                if (WindowHelper.IsIconic(hWnd))
        //                                {
        //                                    WindowHelper.ShowWindow(hWnd, WindowHelper.SW_RESTORE);
        //                                }

        //                                // 将窗口置于前台
        //                                WindowHelper.SetForegroundWindow(hWnd);
        //                                return false; // 停止枚举
        //                            }
        //                        }

        //                        // 发送自定义消息给窗口，通知它显示
        //                        WindowHelper.SendMessage(hWnd, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
        //                        windowFound = true;
        //                        return false; // 停止枚举
        //                    }

        //                    return true; // 继续枚举
        //                }, IntPtr.Zero);

        //                // 如果找到窗口，则退出函数
        //                if (windowFound)
        //                {
        //                    break;
        //                }
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        Debug.WriteLine($"激活已有实例时出错: {ex.Message}");
        //    }
        //}

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

        public static void SetWindowIdentifier(IntPtr hwnd)
        {
            // 设置一个自定义属性，用于识别我们的窗口
            WindowHelper.SetProp(hwnd, WindowIdentifier, hwnd);
        }
    }
}
