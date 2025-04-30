using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services
{
    public class TaskbarThumbnailService : IDisposable
    {
        #region COM接口和结构定义

        // 定义ITaskbarList3接口
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
            void SetProgressValue(IntPtr hwnd, UInt64 ullCompleted, UInt64 ullTotal);
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

        // THUMBBUTTON结构体
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct THUMBBUTTON
        {
            [MarshalAs(UnmanagedType.U4)]
            public THBMASK dwMask;
            public uint iId;
            public uint iBitmap;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szTip;
            [MarshalAs(UnmanagedType.U4)]
            public THBFLAGS dwFlags;
        }

        // TaskbarList CLSID
        [ComImport]
        [Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
        [ClassInterface(ClassInterfaceType.None)]
        private class TaskbarList { }

        // 按钮掩码枚举
        [Flags]
        private enum THBMASK
        {
            THB_BITMAP = 0x1,
            THB_ICON = 0x2,
            THB_TOOLTIP = 0x4,
            THB_FLAGS = 0x8
        }

        // 按钮标志枚举
        [Flags]
        private enum THBFLAGS
        {
            THBF_ENABLED = 0,
            THBF_DISABLED = 0x1,
            THBF_DISMISSONCLICK = 0x2,
            THBF_NOBACKGROUND = 0x4,
            THBF_HIDDEN = 0x8,
            THBF_NONINTERACTIVE = 0x10
        }

        // 任务栏进度标记枚举
        public enum TBPFLAG
        {
            TBPF_NOPROGRESS = 0,
            TBPF_INDETERMINATE = 0x1,
            TBPF_NORMAL = 0x2,
            TBPF_ERROR = 0x4,
            TBPF_PAUSED = 0x8
        }

        #endregion

        #region 图标加载相关P/Invoke

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr GetModuleHandle(string lpModuleName);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint LR_SHARED = 0x00008000;

        #endregion

        #region 常量定义

        public const int WM_COMMAND = 0x0111;
        public const int THBN_CLICKED = 0x1800;

        #endregion

        #region 事件和委托

        // 缩略图按钮点击事件参数
        public class ThumbButtonClickedEventArgs : EventArgs
        {
            public int ButtonId { get; }

            public ThumbButtonClickedEventArgs(int buttonId)
            {
                ButtonId = buttonId;
            }
        }

        // 缩略图按钮点击事件委托
        public delegate void ThumbButtonClickedEventHandler(object sender, ThumbButtonClickedEventArgs e);

        // 缩略图按钮点击事件
        public event ThumbButtonClickedEventHandler ThumbButtonClicked;

        #endregion

        #region 私有字段

        private readonly ITaskbarList3 _taskbarList;
        private readonly IntPtr _windowHandle;
        private readonly Dictionary<int, ThumbnailButton> _buttons = new Dictionary<int, ThumbnailButton>();
        private bool _isInitialized = false;

        #endregion

        #region 公共属性和方法

        /// <summary>
        /// 公共缩略图按钮类
        /// </summary>
        public class ThumbnailButton
        {
            public int Id { get; set; }
            public string Tooltip { get; set; }
            public string IconPath { get; set; }
            public bool Enabled { get; set; } = true;
            public bool Visible { get; set; } = true;
            public bool DismissOnClick { get; set; } = false;

            // 保存已加载的图标句柄
            internal IntPtr IconHandle { get; set; } = IntPtr.Zero;
        }

        /// <summary>
        /// 创建TaskbarThumbnailService的实例
        /// </summary>
        /// <param name="window">WinUI窗口</param>
        public TaskbarThumbnailService(Window window)
        {
            // 获取窗口句柄
            _windowHandle = WindowNative.GetWindowHandle(window);

            // 初始化任务栏接口
            _taskbarList = (ITaskbarList3)new TaskbarList();
            _taskbarList.HrInit();
        }

        /// <summary>
        /// 添加缩略图按钮
        /// </summary>
        /// <param name="buttons">要添加的按钮列表</param>
        public void AddButtons(IEnumerable<ThumbnailButton> buttons)
        {
            foreach (var button in buttons)
            {
                _buttons[button.Id] = button;

                // 加载图标
                if (!string.IsNullOrEmpty(button.IconPath))
                {
                    button.IconHandle = LoadIconFromFile(button.IconPath);
                }
            }

            // 更新任务栏按钮
            UpdateTaskbarButtons();
            _isInitialized = true;
        }

        /// <summary>
        /// 更新缩略图按钮
        /// </summary>
        /// <param name="buttonId">按钮ID</param>
        /// <param name="enabled">是否启用</param>
        /// <param name="visible">是否可见</param>
        public void UpdateButton(int buttonId, bool? enabled = null, bool? visible = null)
        {
            if (_buttons.TryGetValue(buttonId, out var button))
            {
                if (enabled.HasValue)
                    button.Enabled = enabled.Value;

                if (visible.HasValue)
                    button.Visible = visible.Value;

                // 更新任务栏按钮
                UpdateTaskbarButtons();
            }
        }

        /// <summary>
        /// 设置任务栏进度状态
        /// </summary>
        /// <param name="state">进度状态</param>
        public void SetProgressState(TBPFLAG state)
        {
            _taskbarList.SetProgressState(_windowHandle, state);
        }

        /// <summary>
        /// 设置任务栏进度值
        /// </summary>
        /// <param name="current">当前进度</param>
        /// <param name="total">总进度</param>
        public void SetProgressValue(ulong current, ulong total)
        {
            _taskbarList.SetProgressValue(_windowHandle, current, total);
        }

        /// <summary>
        /// 设置缩略图提示文本
        /// </summary>
        /// <param name="tooltip">提示文本</param>
        public void SetThumbnailTooltip(string tooltip)
        {
            _taskbarList.SetThumbnailTooltip(_windowHandle, tooltip);
        }

        /// <summary>
        /// 处理窗口消息
        /// </summary>
        /// <param name="messageId">消息ID</param>
        /// <param name="wParam">消息参数</param>
        /// <param name="lParam">消息参数</param>
        /// <returns>是否处理了消息</returns>
        public bool HandleWindowMessage(int messageId, IntPtr wParam, IntPtr lParam)
        {
            if (messageId == WM_COMMAND)
            {
                // 高位字是通知代码
                int notifyCode = (int)((wParam.ToInt64() >> 16) & 0xFFFF);
                // 低位字是按钮ID
                int buttonId = (int)(wParam.ToInt64() & 0xFFFF);

                // 处理缩略图按钮点击
                if (notifyCode == THBN_CLICKED)
                {
                    ThumbButtonClicked?.Invoke(this, new ThumbButtonClickedEventArgs(buttonId));
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 释放资源
        /// </summary>
        public void Dispose()
        {
            // 释放图标资源
            foreach (var button in _buttons.Values)
            {
                if (button.IconHandle != IntPtr.Zero)
                {
                    DestroyIcon(button.IconHandle);
                    button.IconHandle = IntPtr.Zero;
                }
            }

            // 清除按钮字典
            _buttons.Clear();

            // 如果有必要，还可以释放其他COM相关资源
            GC.SuppressFinalize(this);
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 更新任务栏按钮
        /// </summary>
        private void UpdateTaskbarButtons()
        {
            if (_buttons.Count == 0)
                return;

            // 创建数组存放按钮
            THUMBBUTTON[] nativeButtons = new THUMBBUTTON[_buttons.Count];
            int index = 0;

            foreach (var pair in _buttons)
            {
                var button = pair.Value;

                // 设置按钮属性
                nativeButtons[index].dwMask = THBMASK.THB_ICON | THBMASK.THB_TOOLTIP | THBMASK.THB_FLAGS;
                nativeButtons[index].iId = (uint)button.Id;
                nativeButtons[index].szTip = button.Tooltip ?? string.Empty;

                // 设置按钮标志
                THBFLAGS flags = THBFLAGS.THBF_ENABLED;

                if (!button.Enabled)
                    flags |= THBFLAGS.THBF_DISABLED;

                if (!button.Visible)
                    flags |= THBFLAGS.THBF_HIDDEN;

                if (button.DismissOnClick)
                    flags |= THBFLAGS.THBF_DISMISSONCLICK;

                nativeButtons[index].dwFlags = flags;
                nativeButtons[index].hIcon = button.IconHandle;

                index++;
            }

            // 如果是第一次添加按钮，使用ThumbBarAddButtons
            if (!_isInitialized)
            {
                _taskbarList.ThumbBarAddButtons(_windowHandle, (uint)nativeButtons.Length, nativeButtons);
            }
            else
            {
                // 否则更新现有按钮
                _taskbarList.ThumbBarUpdateButtons(_windowHandle, (uint)nativeButtons.Length, nativeButtons);
            }
        }

        /// <summary>
        /// 从文件加载图标
        /// </summary>
        /// <param name="path">图标文件路径</param>
        /// <returns>图标句柄</returns>
        private IntPtr LoadIconFromFile(string path)
        {
            return LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0, LR_LOADFROMFILE | LR_SHARED);
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        #endregion
    }
}
