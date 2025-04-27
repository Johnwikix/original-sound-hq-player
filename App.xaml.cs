using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WinRT.Interop;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        private static Mutex _mutex = null;
        private const string MutexName = "WinUIMusicPlayer_SingleInstanceMutex";
        public static MainWindow MainWindow { get; private set; }
        private Window _tempWindow = null;
        public const int WM_SHOWME = 0x8001;
        /// <summary>
        /// Initializes the singleton application object.  This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            this.InitializeComponent();
        }

        /// <summary>
        /// Invoked when the application is launched.
        /// </summary>
        /// <param name="args">Details about the launch request and process.</param>
        protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
        {
            try
            {
                bool mutexCreated;
                _mutex = new Mutex(true, MutexName, out mutexCreated);

                // 如果互斥量已经存在，则表示应用已经在运行
                if (!mutexCreated)
                {
                    // 显示应用已在运行的弹窗
                    ShowAlreadyRunningDialogAsync();
                    return;
                }

                MainWindow = new MainWindow();
                MainWindow.Activate();
            }
            catch (Exception ex)
            {
                // 记录错误信息
                Debug.WriteLine($"激活窗口时出错: {ex.Message}");
            }
        }

        private async void ShowAlreadyRunningDialogAsync()
        {
            try
            {
                ActivateExistingInstance();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"显示已运行对话框时出错: {ex.Message}");
                ActivateExistingInstance();
                Environment.Exit(0);
            }
        }

        private void ActivateExistingInstance()
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


        // Win32 API 声明
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
    }
}
