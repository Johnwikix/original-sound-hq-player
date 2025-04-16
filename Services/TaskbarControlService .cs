using Microsoft.UI.Xaml;
using System;
using System.Runtime.InteropServices;

namespace WinUIMusicPlayer.Services
{
    public class TaskbarControlService : IDisposable
    {
        // 定义事件处理器
        public event EventHandler PlayPauseClicked;
        public event EventHandler PreviousClicked;
        public event EventHandler NextClicked;

        // 窗口句柄
        private IntPtr _windowHandle;

        // COM对象
        private ITaskbarList3 _taskbarList;

        // 子类化窗口过程的ID
        private uint _subclassId = 1;

        // 按钮状态
        private bool _isPlaying = false;

        // 按钮ID常量
        private const int THBID_PREV = 0;
        private const int THBID_PLAYPAUSE = 1;
        private const int THBID_NEXT = 2;

        public TaskbarControlService(Window window)
        {
            // 获取窗口句柄
            _windowHandle = WindowNative.GetWindowHandle(window);

            // 初始化任务栏按钮
            InitializeTaskbarThumbnailButtons();

            // 子类化窗口过程来处理缩略图按钮消息
            SetWindowSubclass(_windowHandle, WindowSubclassProc, _subclassId, IntPtr.Zero);
        }

        private void InitializeTaskbarThumbnailButtons()
        {
            try
            {
                // 创建TaskbarList COM对象
                _taskbarList = (ITaskbarList3)new TaskbarList();
                _taskbarList.HrInit();

                // 定义三个按钮：上一曲、播放/暂停、下一曲
                THUMBBUTTON[] buttons = new THUMBBUTTON[3];

                // 上一曲按钮
                buttons[THBID_PREV] = new THUMBBUTTON
                {
                    dwMask = THBMASK.THB_ICON | THBMASK.THB_TOOLTIP | THBMASK.THB_FLAGS,
                    iId = THBID_PREV,
                    dwFlags = THBFLAGS.THBF_ENABLED,
                    hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32739), // 系统左箭头图标
                    szTip = "上一曲"
                };

                // 播放/暂停按钮
                buttons[THBID_PLAYPAUSE] = new THUMBBUTTON
                {
                    dwMask = THBMASK.THB_ICON | THBMASK.THB_TOOLTIP | THBMASK.THB_FLAGS,
                    iId = THBID_PLAYPAUSE,
                    dwFlags = THBFLAGS.THBF_ENABLED,
                    hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32739 + 1), // 系统播放图标
                    szTip = "播放"
                };

                // 下一曲按钮
                buttons[THBID_NEXT] = new THUMBBUTTON
                {
                    dwMask = THBMASK.THB_ICON | THBMASK.THB_TOOLTIP | THBMASK.THB_FLAGS,
                    iId = THBID_NEXT,
                    dwFlags = THBFLAGS.THBF_ENABLED,
                    hIcon = LoadIcon(IntPtr.Zero, (IntPtr)32738), // 系统右箭头图标
                    szTip = "下一曲"
                };

                // 添加按钮到任务栏缩略图
                _taskbarList.ThumbBarAddButtons(_windowHandle, (uint)buttons.Length, buttons);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化任务栏缩略图按钮失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 更新播放/暂停按钮状态
        /// </summary>
        public void UpdatePlayState(bool isPlaying)
        {
            _isPlaying = isPlaying;

            try
            {
                THUMBBUTTON button = new THUMBBUTTON
                {
                    dwMask = THBMASK.THB_ICON | THBMASK.THB_TOOLTIP | THBMASK.THB_FLAGS,
                    iId = THBID_PLAYPAUSE,
                    dwFlags = THBFLAGS.THBF_ENABLED,
                    hIcon = LoadIcon(IntPtr.Zero, (IntPtr)(isPlaying ? 32739 + 2 : 32739 + 1)), // 暂停/播放图标
                    szTip = isPlaying ? "暂停" : "播放"
                };

                // 修正：移除 ref 关键字
                _taskbarList.ThumbBarUpdateButtons(_windowHandle, 1, new[] { button });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"更新任务栏按钮状态失败: {ex.Message}");
            }
        }

        // 修正：重命名为 WindowSubclassProc 来避免与委托类型冲突
        private IntPtr WindowSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData)
        {
            const int WM_COMMAND = 0x0111;
            const int THBN_CLICKED = 0x1800;

            if (uMsg == WM_COMMAND)
            {
                int notifyCode = wParam.ToInt32() >> 16;
                int buttonId = wParam.ToInt32() & 0xffff;

                if (notifyCode == THBN_CLICKED)
                {
                    switch (buttonId)
                    {
                        case THBID_PREV:
                            PreviousClicked?.Invoke(this, EventArgs.Empty);
                            break;
                        case THBID_PLAYPAUSE:
                            _isPlaying = !_isPlaying;
                            UpdatePlayState(_isPlaying);
                            PlayPauseClicked?.Invoke(this, EventArgs.Empty);
                            break;
                        case THBID_NEXT:
                            NextClicked?.Invoke(this, EventArgs.Empty);
                            break;
                    }
                }
            }

            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_windowHandle != IntPtr.Zero)
            {
                RemoveWindowSubclass(_windowHandle, WindowSubclassProc, _subclassId);
            }

            if (_taskbarList != null)
            {
                Marshal.ReleaseComObject(_taskbarList);
                _taskbarList = null;
            }
        }

        #region P/Invoke 和 COM 定义

        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList { }

        [ComImport]
        [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ITaskbarList3
        {
            // ITaskbarList
            void HrInit();
            void AddTab(IntPtr hwnd);
            void DeleteTab(IntPtr hwnd);
            void ActivateTab(IntPtr hwnd);
            void SetActiveAlt(IntPtr hwnd);

            // ITaskbarList2
            void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

            // ITaskbarList3
            void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
            void SetProgressState(IntPtr hwnd, TBPFLAG tbpFlags);
            void RegisterTab(IntPtr hwndTab, IntPtr hwndMDI);
            void UnregisterTab(IntPtr hwndTab);
            void SetTabOrder(IntPtr hwndTab, IntPtr hwndInsertBefore);
            void SetTabActive(IntPtr hwndTab, IntPtr hwndMDI, uint dwReserved);
            void ThumbBarAddButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
            void ThumbBarUpdateButtons(IntPtr hwnd, uint cButtons, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] THUMBBUTTON[] pButtons);
            void ThumbBarSetImageList(IntPtr hwnd, IntPtr himl);
            void SetOverlayIcon(IntPtr hwnd, IntPtr hIcon, [MarshalAs(UnmanagedType.LPWStr)] string pszDescription);
            void SetThumbnailTooltip(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pszTip);
            void SetThumbnailClip(IntPtr hwnd, IntPtr prcClip);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct THUMBBUTTON
        {
            public THBMASK dwMask;
            public uint iId;
            public THBFLAGS dwFlags;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;
        }

        [Flags]
        private enum THBMASK : uint
        {
            THB_BITMAP = 0x1,
            THB_ICON = 0x2,
            THB_TOOLTIP = 0x4,
            THB_FLAGS = 0x8
        }

        [Flags]
        private enum THBFLAGS : uint
        {
            THBF_ENABLED = 0,
            THBF_DISABLED = 0x1,
            THBF_DISMISSONCLICK = 0x2,
            THBF_NOBACKGROUND = 0x4,
            THBF_HIDDEN = 0x8,
            THBF_NONINTERACTIVE = 0x10
        }

        private enum TBPFLAG
        {
            TBPF_NOPROGRESS = 0,
            TBPF_INDETERMINATE = 0x1,
            TBPF_NORMAL = 0x2,
            TBPF_ERROR = 0x4,
            TBPF_PAUSED = 0x8
        }

        // P/Invoke 定义
        [DllImport("comctl32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass, IntPtr dwRefData);

        [DllImport("comctl32.dll")]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, uint uIdSubclass);

        [DllImport("comctl32.dll")]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, IntPtr uIdSubclass, IntPtr dwRefData);

        #endregion
    }

    // WindowNative 的定义，用于获取窗口句柄
    internal static class WindowNative
    {
        [DllImport("user32.dll")]
        private static extern IntPtr GetActiveWindow();

        public static IntPtr GetWindowHandle(object target)
        {
            // 从 WinUI 3 窗口获取句柄
            return GetActiveWindow();
        }
    }
}