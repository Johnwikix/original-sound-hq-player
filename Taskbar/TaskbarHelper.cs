using Microsoft.Extensions.DependencyInjection;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Taskbar
{
    // ── 修复说明 ─────────────────────────────────────────────────────────────────
    // 崩溃原因（c0000005 Access Violation）：
    //   Win32 THUMBBUTTON 在 x64 下，iBitmap(uint) 之后、hIcon(IntPtr) 之前
    //   存在 4 字节隐式 padding（满足 8 字节指针对齐），导致后续字段整体错位。
    //   CLR 在没有显式 _pad 字段的情况下，默认 Pack 不会补这段 padding，
    //   COM vtable 因此读到错误偏移，写入野指针地址，触发 AV。
    //
    // 修复内容：
    //   1. 在 iBitmap 与 hIcon 之间插入 private uint _pad 对齐字段。
    //   2. 添加静态断言确认结构体大小为 552 字节（x64 Win32 THUMBBUTTON 大小）。
    //   3. RecoverTaskbarHelper() 改为重建 COM 对象后重新 AddButtons。
    //   4. 补全所有 try-catch 覆盖缺口（见各方法注释）。
    //   5. 新增 ErrorOccurred 事件，所有 catch 块统一通过 OnError() 触发，
    //      调用方可订阅后写日志、弹提示、上报遥测，无需在此类内硬编码输出逻辑。
    // ─────────────────────────────────────────────────────────────────────────────

    /// <summary>TaskbarHelper 内部错误的事件参数。</summary>
    public sealed class TaskbarErrorEventArgs : EventArgs
    {
        /// <summary>错误发生的方法名（由 CallerMemberName 自动填充）。</summary>
        public string Source { get; }

        /// <summary>触发事件的异常对象（始终非 null）。</summary>
        public Exception Exception { get; }

        /// <summary>
        /// 错误是否已被妥善处理、可以安全忽略。
        /// true  = 已降级处理，程序可以继续运行（Dispose 路径、图标更新失败等）。
        /// false = 严重错误，任务栏功能可能已完全失效（初始化失败、COM 创建失败等）。
        /// </summary>
        public bool IsRecoverable { get; }

        internal TaskbarErrorEventArgs(string source, Exception exception, bool isRecoverable)
        {
            Source = source;
            Exception = exception;
            IsRecoverable = isRecoverable;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal unsafe struct NativeThumbButton
    {
        public ThumbButtonMask dwMask;      // 4 bytes, offset  0
        public uint iId;                    // 4 bytes, offset  4
        public uint iBitmap;               // 4 bytes, offset  8
        private uint _pad;                 // 4 bytes, offset 12  ← 关键：补充 x64 隐式对齐 padding
        public IntPtr hIcon;               // 8 bytes, offset 16
        public fixed char szTip[260];      // 520 bytes, offset 24  (WCHAR[260])
        public ThumbButtonFlags dwFlags;   // 4 bytes, offset 544
        // CLR 补 4 字节尾部 padding → 结构体总大小 = 552 字节

        public void SetTip(string tip)
        {
            if (tip == null) return;
            fixed (char* p = szTip)
            {
                int len = Math.Min(tip.Length, 259);
                for (int i = 0; i < len; i++)
                    p[i] = tip[i];
                p[len] = '\0';
            }
        }
    }

    public unsafe partial class TaskbarHelper : IDisposable
    {
        // ── 静态构造：断言结构体大小，尽早发现布局问题 ──────────────────────────
        static TaskbarHelper()
        {
            int actualSize = sizeof(NativeThumbButton);
            System.Diagnostics.Debug.Assert(
                actualSize == 552,
                $"[TaskbarHelper] NativeThumbButton 大小错误: {actualSize} bytes，期望 552 bytes (x64)。" +
                $"结构体布局与 Win32 THUMBBUTTON 不一致，会导致 COM 调用崩溃。");

            if (actualSize != 552)
                throw new InvalidOperationException(
                    $"NativeThumbButton 布局错误，实际大小 {actualSize} bytes，期望 552 bytes。");
        }

        // ── 消息常量 ────────────────────────────────────────────────────────────
        private const int WM_COMMAND = 0x0111;
        private const int THBN_CLICKED = 0x1800;

        // ── P/Invoke ────────────────────────────────────────────────────────────

        [LibraryImport("Comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetWindowSubclass(
            IntPtr hWnd,
            delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> pfnSubclass,
            IntPtr uIdSubclass,
            IntPtr dwRefData);

        [LibraryImport("Comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool RemoveWindowSubclass(
            IntPtr hWnd,
            delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, IntPtr, IntPtr, IntPtr> pfnSubclass,
            IntPtr uIdSubclass);

        [LibraryImport("Comctl32.dll", SetLastError = true)]
        private static partial IntPtr DefSubclassProc(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [LibraryImport("user32.dll", EntryPoint = "LoadIconW", SetLastError = true)]
        private static partial IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

        [LibraryImport("user32.dll", EntryPoint = "LoadImageW", SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr LoadImage(
            IntPtr hinst, string lpszName,
            uint uType, int cxDesired, int cyDesired, uint fuLoad);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyIcon(IntPtr hIcon);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("ole32.dll")]
        private static partial int CoCreateInstance(
            in Guid rclsid, IntPtr pUnkOuter,
            uint dwClsContext, in Guid riid, out IntPtr ppv);

        // ── COM vtable 槽位 ─────────────────────────────────────────────────────
        //
        // IUnknown      : 0=QueryInterface  1=AddRef        2=Release
        // ITaskbarList  : 3=HrInit          4=AddTab        5=DeleteTab
        //                 6=ActivateTab     7=SetActiveAlt
        // ITaskbarList2 : 8=MarkFullscreenWindow
        // ITaskbarList3 : 9=SetProgressValue   10=SetProgressState
        //                 11=RegisterTab        12=UnregisterTab
        //                 13=SetTabOrder        14=SetTabActive
        //                 15=ThumbBarAddButtons 16=ThumbBarUpdateButtons
        //                 17=ThumbBarSetImageList
        //                 18=SetOverlayIcon     19=SetThumbnailTooltip
        //                 20=SetThumbnailClip
        private const int SLOT_Release = 2;
        private const int SLOT_HrInit = 3;
        private const int SLOT_ThumbBarAddButtons = 15;
        private const int SLOT_ThumbBarUpdateButtons = 16;

        private static readonly Guid CLSID_TaskbarList =
            new("56FDF344-FD6D-11d0-958A-006097C9A090");
        private static readonly Guid IID_ITaskbarList3 =
            new("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf");

        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const int LR_LOADFROMFILE = 0x0010;
        private const int LR_DEFAULTSIZE = 0x0040;
        private const uint IMAGE_ICON = 1;
        private static readonly IntPtr IDI_APPLICATION = (IntPtr)32512;

        // ── 字段 ────────────────────────────────────────────────────────────────

        private IntPtr _hwnd;
        private IntPtr _pTaskbarList;
        private readonly IntPtr[] _iconHandles = new IntPtr[3];
        private NativeThumbButton[] _nativeButtons;

        private GCHandle _selfHandle;
        private bool _isSubclassed = false;
        private bool _buttonsAdded = false;
        private bool _isDisposed = false;
        private bool _isCurrentPlaying = false;

        private MusicBrowseViewModel _musicBrowseViewModel;

        // ── 错误事件 ─────────────────────────────────────────────────────────────
        // 所有 catch 块均通过 OnError() 触发此事件，调用方统一订阅处理。
        // 注意：WindowSubclassProc 是 [UnmanagedCallersOnly] 静态方法，
        //       无法直接访问实例事件；其内部通过取到 helper 实例后调用 helper.OnError()。

        /// <summary>
        /// 任务栏辅助类内部发生错误时触发。
        /// <para>IsRecoverable=true：已降级处理，程序可继续运行。</para>
        /// <para>IsRecoverable=false：严重错误，任务栏功能可能已失效。</para>
        /// </summary>
        public event EventHandler<TaskbarErrorEventArgs> ErrorOccurred;

        /// <summary>统一触发 ErrorOccurred 并写 Debug 输出。</summary>
        private void OnError(Exception ex, bool isRecoverable,
            [System.Runtime.CompilerServices.CallerMemberName] string source = "")
        {
            System.Diagnostics.Debug.WriteLine(
                $"[TaskbarHelper.{source}] {(isRecoverable ? "可恢复" : "严重")}错误: {ex}");
            try
            {
                ErrorOccurred?.Invoke(this, new TaskbarErrorEventArgs(source, ex, isRecoverable));
            }
            catch
            {
            }
        }

        // ── 构造 ────────────────────────────────────────────────────────────────

        public TaskbarHelper(IntPtr hwnd, MusicBrowseViewModel musicBrowseViewModel)
        {
            _hwnd = hwnd;
            _musicBrowseViewModel = musicBrowseViewModel;
        }

        // ── 公共方法 ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 任务栏重建后调用（如 Explorer 重启）。
        /// 必须使用 UpdateButtons 而非 AddButtons，因为 AddButtons 在同一窗口只能调用一次。
        /// 任务栏重建后需重新 AddButtons，所以这里先重置标记再重新初始化。
        /// </summary>
        public void RecoverTaskbarHelper()
        {
            // 缺口①：Explorer 重启后 COM 对象已失效，Release 本身可能 AV，必须保护
            if (_pTaskbarList != IntPtr.Zero)
            {
                try { CallRelease(_pTaskbarList); }
                catch (Exception ex)
                {
                    OnError(ex, isRecoverable: true);
                }
                finally { _pTaskbarList = IntPtr.Zero; }
            }

            _buttonsAdded = false;

            // 缺口②：_nativeButtons 为 null 时（InitializeThumbButtons 从未成功过），
            //         AddButtons 传入 null 会立刻 NullReferenceException，直接退出恢复流程
            if (_nativeButtons == null)
            {
                System.Diagnostics.Debug.WriteLine("[TaskbarHelper.RecoverTaskbarHelper] _nativeButtons 为 null，跳过恢复");
                return;
            }

            try
            {
                int hr = CoCreateInstance(
                    CLSID_TaskbarList, IntPtr.Zero,
                    CLSCTX_INPROC_SERVER, IID_ITaskbarList3,
                    out _pTaskbarList);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                CallHrInit(_pTaskbarList);

                // 任务栏重建后必须重新 AddButtons（不能用 UpdateButtons）
                CallThumbBarAddButtons(_pTaskbarList, _hwnd, _nativeButtons);
                _buttonsAdded = true;
            }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: false);
            }
        }

        public void InitializeThumbButtons()
        {
            try
            {
                int hr = CoCreateInstance(
                    CLSID_TaskbarList, IntPtr.Zero,
                    CLSCTX_INPROC_SERVER, IID_ITaskbarList3,
                    out _pTaskbarList);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);

                CallHrInit(_pTaskbarList);

                _isCurrentPlaying = AppData.IsPlaying;

                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                _iconHandles[0] = CreateIconFromImage(System.IO.Path.Combine(appDir, "Assets\\last.ico"), 32);
                _iconHandles[1] = AppData.IsPlaying
                    ? CreateIconFromImage(System.IO.Path.Combine(appDir, "Assets\\stop.ico"), 32)
                    : CreateIconFromImage(System.IO.Path.Combine(appDir, "Assets\\play.ico"), 32);
                _iconHandles[2] = CreateIconFromImage(System.IO.Path.Combine(appDir, "Assets\\next.ico"), 32);

                _nativeButtons = new NativeThumbButton[3];

                _nativeButtons[0].dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.THB_FLAGS;
                _nativeButtons[0].iId = 0;
                _nativeButtons[0].hIcon = _iconHandles[0];
                _nativeButtons[0].dwFlags = ThumbButtonFlags.Enabled;
                _nativeButtons[0].SetTip(ToolUtils.GetString("LastSong"));

                _nativeButtons[1].dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.THB_FLAGS;
                _nativeButtons[1].iId = 1;
                _nativeButtons[1].hIcon = _iconHandles[1];
                _nativeButtons[1].dwFlags = ThumbButtonFlags.Enabled;
                _nativeButtons[1].SetTip(ToolUtils.GetString("PlayNPause"));

                _nativeButtons[2].dwMask = ThumbButtonMask.Icon | ThumbButtonMask.Tooltip | ThumbButtonMask.THB_FLAGS;
                _nativeButtons[2].iId = 2;
                _nativeButtons[2].hIcon = _iconHandles[2];
                _nativeButtons[2].dwFlags = ThumbButtonFlags.Enabled;
                _nativeButtons[2].SetTip(ToolUtils.GetString("NextSong"));

                CallThumbBarAddButtons(_pTaskbarList, _hwnd, _nativeButtons);
                _buttonsAdded = true;

                _selfHandle = GCHandle.Alloc(this);
                _isSubclassed = SetWindowSubclass(
                    _hwnd, &WindowSubclassProc, (IntPtr)1,
                    GCHandle.ToIntPtr(_selfHandle));

                if (!_isSubclassed)
                {
                    var win32Err = Marshal.GetLastWin32Error();
                    OnError(new InvalidOperationException($"SetWindowSubclass 失败，Win32 错误码: {win32Err}"),
                            isRecoverable: false);
                }
            }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: false);
                throw;
            }
        }

        public void UpdateButtonIcon(int buttonId, string iconPath, int size = 32)
        {
            if (!_buttonsAdded || _pTaskbarList == IntPtr.Zero) return;

            try
            {
                IntPtr newIcon = CreateIconFromImage(iconPath, size);
                IntPtr oldIcon = _iconHandles[buttonId];
                _iconHandles[buttonId] = newIcon;
                _nativeButtons[buttonId].hIcon = newIcon;
                CallThumbBarUpdateButtons(_pTaskbarList, _hwnd, _nativeButtons);
                if (oldIcon != IntPtr.Zero)
                    DestroyIcon(oldIcon);
            }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: true);
            }
        }

        public void UpdateTaskbarButtonIcon()
        {
            // 缺口③：_isDisposed 或 _nativeButtons 为 null 时直接 return，
            //         避免在 Dispose 之后被外部计时器/事件回调触发而崩溃
            if (_isDisposed || _nativeButtons == null) return;

            if (_isCurrentPlaying != AppData.IsPlaying)
            {
                _isCurrentPlaying = AppData.IsPlaying;
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string newIconPath = AppData.IsPlaying
                    ? System.IO.Path.Combine(appDir, "Assets\\stop.ico")
                    : System.IO.Path.Combine(appDir, "Assets\\play.ico");
                UpdateButtonIcon(1, newIconPath);
            }
        }

        // ── 静态子类回调 ─────────────────────────────────────────────────────────

        // 缺口④：[UnmanagedCallersOnly] 函数内如果托管异常逃逸到非托管调用方，
        //         CLR 无法展开非托管帧，直接调用 FailFast 终止进程（即使外层有 try-catch 也救不了）。
        //         必须在此函数内部 catch-all，绝不让任何异常逃出。
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr WindowSubclassProc(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            IntPtr uIdSubclass, IntPtr dwRefData)
        {
            TaskbarHelper helper = null;
            try
            {
                if (dwRefData != IntPtr.Zero)
                {
                    var handle = GCHandle.FromIntPtr(dwRefData);
                    helper = handle.Target as TaskbarHelper;
                    if (helper != null)
                        return helper.HandleWindowMessage(hWnd, uMsg, wParam, lParam);
                }
            }
            catch (Exception ex)
            {
                // 不能 rethrow：异常逃出 [UnmanagedCallersOnly] 函数 = FailFast
                // 优先用实例的 OnError 触发事件；取不到实例时退化到 Debug.WriteLine
                if (helper != null)
                    helper.OnError(ex, isRecoverable: true);
                else
                    System.Diagnostics.Debug.WriteLine($"[TaskbarHelper.WindowSubclassProc] 未处理异常（已拦截）: {ex}");
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        // 缺口⑤：消息泵内的异常同样会经由 WindowSubclassProc 逃逸到非托管层，
        //         即便 WindowSubclassProc 有 catch-all，把异常控制在更靠近根源的地方更安全。
        private IntPtr HandleWindowMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (uMsg == WM_COMMAND)
                {
                    int notifyCode = ((int)wParam >> 16) & 0xFFFF;
                    int buttonId = ((int)wParam) & 0xFFFF;
                    if (notifyCode == THBN_CLICKED)
                    {
                        HandleThumbButtonClick(buttonId);
                        return IntPtr.Zero;
                    }
                }
            }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: true);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void HandleThumbButtonClick(int buttonId)
        {
            // 缺口⑥：ViewModel 方法抛出异常（如播放器未初始化、资源未就绪）会经由
            //         消息泵一路逃逸到非托管层，导致 FailFast。
            try
            {
                switch (buttonId)
                {
                    case 0: _musicBrowseViewModel.LastMusicButton_Click(); break;
                    case 1: _musicBrowseViewModel.PlayButton_Click(); break;
                    case 2: _musicBrowseViewModel.NextMusicButton_Click(); break;
                }
            }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: true);
            }

            if (buttonId == 1)
            {
                try
                {
                    string appDir = AppDomain.CurrentDomain.BaseDirectory;
                    string newIconPath = AppData.IsPlaying
                        ? System.IO.Path.Combine(appDir, "Assets\\stop.ico")
                        : System.IO.Path.Combine(appDir, "Assets\\play.ico");
                    UpdateButtonIcon(1, newIconPath);
                }
                catch (Exception ex)
                {
                    OnError(ex, isRecoverable: true);
                }
            }
        }

        // ── COM vtable 调用 ──────────────────────────────────────────────────────

        private static void CallHrInit(IntPtr pUnk)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)pUnk)[SLOT_HrInit];
            int hr = fn(pUnk);
            if (hr != 0) Marshal.ThrowExceptionForHR(hr);
        }

        private static void CallThumbBarAddButtons(
            IntPtr pUnk, IntPtr hwnd, NativeThumbButton[] buttons)
        {
            fixed (NativeThumbButton* pButtons = buttons)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, NativeThumbButton*, int>)
                    (*(IntPtr**)pUnk)[SLOT_ThumbBarAddButtons];
                int hr = fn(pUnk, hwnd, (uint)buttons.Length, pButtons);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        private static void CallThumbBarUpdateButtons(
            IntPtr pUnk, IntPtr hwnd, NativeThumbButton[] buttons)
        {
            fixed (NativeThumbButton* pButtons = buttons)
            {
                var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, NativeThumbButton*, int>)
                    (*(IntPtr**)pUnk)[SLOT_ThumbBarUpdateButtons];
                int hr = fn(pUnk, hwnd, (uint)buttons.Length, pButtons);
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
            }
        }

        private static void CallRelease(IntPtr pUnk)
        {
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(IntPtr**)pUnk)[SLOT_Release];
            fn(pUnk);
        }

        // ── 图标辅助 ─────────────────────────────────────────────────────────────

        private static IntPtr CreateIconFromImage(string imagePath, int size = 16)
        {
            if (!System.IO.File.Exists(imagePath))
            {
                System.Diagnostics.Debug.WriteLine($"[TaskbarHelper.CreateIconFromImage] 图片文件不存在: {imagePath}");
                return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            }
            try
            {
                IntPtr hIcon = LoadImage(
                    IntPtr.Zero, imagePath, IMAGE_ICON,
                    size, size, LR_LOADFROMFILE | LR_DEFAULTSIZE);
                if (hIcon == IntPtr.Zero)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[TaskbarHelper.CreateIconFromImage] 加载图片失败，错误码: {Marshal.GetLastWin32Error()}，路径: {imagePath}");
                    return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
                }
                return hIcon;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TaskbarHelper.CreateIconFromImage] 出错: {ex.Message}，路径: {imagePath}");
                return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
            }
        }

        private void ReleaseIcons()
        {
            foreach (IntPtr h in _iconHandles)
                if (h != IntPtr.Zero) DestroyIcon(h);
        }

        // ── Dispose ──────────────────────────────────────────────────────────────

        ~TaskbarHelper() => Dispose(false);

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;

            // 缺口⑦：Dispose 路径上任何一步抛出异常都会跳过后续的清理，
            //         导致 GCHandle 泄漏、COM 引用泄漏、图标句柄泄漏。
            //         每一步独立 try-catch，确保全部清理都能执行。

            if (_isSubclassed)
            {
                try
                {
                    if (IsWindow(_hwnd))
                        RemoveWindowSubclass(_hwnd, &WindowSubclassProc, (IntPtr)1);
                }
                catch (Exception ex)
                {
                    OnError(ex, isRecoverable: true);
                }
                finally { _isSubclassed = false; }
            }

            if (_selfHandle.IsAllocated)
            {
                try { _selfHandle.Free(); }
                catch (Exception ex)
                {
                    OnError(ex, isRecoverable: true);
                }
            }

            if (_pTaskbarList != IntPtr.Zero)
            {
                try { CallRelease(_pTaskbarList); }
                catch (Exception ex)
                {
                    OnError(ex, isRecoverable: true);
                }
                finally { _pTaskbarList = IntPtr.Zero; }
            }

            try { ReleaseIcons(); }
            catch (Exception ex)
            {
                OnError(ex, isRecoverable: true);
            }

            _nativeButtons = null;
            _buttonsAdded = false;
            _isDisposed = true;
        }
    }
}