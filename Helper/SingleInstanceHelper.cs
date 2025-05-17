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
                // 尝试从注册表获取主窗口句柄
                IntPtr mainWindowHandle = IntPtr.Zero;
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\SennpeiStudio\OriginalSoundHIFIPlayer"))
                    {
                        if (key != null)
                        {
                            long handleValue = Convert.ToInt64(key.GetValue("MainWindowHandle", 0));
                            if (handleValue != 0)
                            {
                                mainWindowHandle = new IntPtr(handleValue);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"读取窗口句柄失败: {ex.Message}");
                }
                // 如果能找到保存的窗口句柄，直接使用它
                if (mainWindowHandle != IntPtr.Zero)
                {
                    Debug.WriteLine($"使用保存的窗口句柄: {mainWindowHandle}");
                    // 确保窗口存在
                    if (WindowHelper.IsWindow(mainWindowHandle))
                    {
                        if (WindowHelper.IsWindowVisible(mainWindowHandle))
                        {
                            // 如果窗口最小化，则恢复它
                            if (WindowHelper.IsIconic(mainWindowHandle))
                            {
                                WindowHelper.ShowWindow(mainWindowHandle, WindowHelper.SW_RESTORE);
                            }
                            // 将窗口置于前台
                            WindowHelper.SetForegroundWindow(mainWindowHandle);
      
                        }
                        else {
                            Debug.WriteLine($"发送显示窗口消息到保存的句柄: {mainWindowHandle}");
                            WindowHelper.SendMessage(mainWindowHandle, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                            Debug.WriteLine("消息已发送");
                        }                           
                        return;
                    }
                }

                // 如果没有找到保存的句柄，则回退到进程枚举方法
                //Process current = Process.GetCurrentProcess();
                //foreach (Process process in Process.GetProcessesByName(current.ProcessName))
                //{
                //    if (process.Id != current.Id)
                //    {
                //        // 尝试枚举进程的所有窗口
                //        bool windowFound = false;
                //        WindowHelper.EnumWindows((hWnd, lParam) =>
                //        {
                //            uint processId;
                //            WindowHelper.GetWindowThreadProcessId(hWnd, out processId);

                //            if (processId == process.Id)
                //            {
                //                int length = WindowHelper.GetWindowTextLength(hWnd);
                //                if (length > 0)
                //                {
                //                    StringBuilder windowTitle = new StringBuilder(length + 1);
                //                    WindowHelper.GetWindowText(hWnd, windowTitle, windowTitle.Capacity);

                //                    // 检查窗口标题，或者只要找到第一个可见窗口就激活
                //                    if (WindowHelper.IsWindowVisible(hWnd))
                //                    {
                //                        // 如果窗口最小化，则恢复它
                //                        if (WindowHelper.IsIconic(hWnd))
                //                        {
                //                            WindowHelper.ShowWindow(hWnd, WindowHelper.SW_RESTORE);
                //                        }

                //                        // 将窗口置于前台
                //                        WindowHelper.SetForegroundWindow(hWnd);
                //                        return false; // 停止枚举
                //                    }
                //                }

                //                // 发送自定义消息给窗口，通知它显示
                //                Debug.WriteLine($"尝试发送显示窗口消息,句柄:{hWnd}");
                //                WindowHelper.SendMessage(hWnd, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                //                Debug.WriteLine("消息已发送");
                //                windowFound = true;
                //                return false; // 停止枚举
                //            }

                //            return true; // 继续枚举
                //        }, IntPtr.Zero);

                //        // 如果找到窗口，则退出函数
                //        if (windowFound)
                //        {
                //            break;
                //        }
                //    }
                //}
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
    }
}
