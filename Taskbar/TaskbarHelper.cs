using Microsoft.Extensions.DependencyInjection;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Taskbar
{
    // ThumbButton 必须是完全 blittable 的 struct，fixed char[] 代替托管 string
    // 原来的 [MarshalAs(UnmanagedType.ByValTStr)] 在手动 vtable 调用时不会自动 marshal
    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct NativeThumbButton
    {
        public ThumbButtonMask dwMask;
        public uint iId;
        public uint iBitmap;
        public IntPtr hIcon;
        public fixed char szTip[260];   // 内嵌字符数组，与 Win32 结构体完全一致
        public ThumbButtonFlags dwFlags;

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
        // 消息常量
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
        private NativeThumbButton[] _nativeButtons;   // blittable，直接 fixed 传给 COM

        private GCHandle _selfHandle;
        private bool _isSubclassed = false;
        private bool _isDisposed = false;
        private bool _isCurrentPlaying = false;

        private MusicBrowseViewModel _musicBrowseViewModel;

        // ── 构造 ────────────────────────────────────────────────────────────────

        public TaskbarHelper(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _musicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
        }

        // ── 公共方法 ─────────────────────────────────────────────────────────────

        public void RecoverTaskbarHelper()
        {
            CallThumbBarAddButtons(_pTaskbarList, _hwnd, _nativeButtons);
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

                _selfHandle = GCHandle.Alloc(this);
                _isSubclassed = SetWindowSubclass(
                    _hwnd, &WindowSubclassProc, (IntPtr)1,
                    GCHandle.ToIntPtr(_selfHandle));

                if (!_isSubclassed)
                    System.Diagnostics.Debug.WriteLine(
                        $"设置窗口子类失败，错误码: {Marshal.GetLastWin32Error()}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化任务栏按钮出错: {ex.Message}");
                throw;
            }
        }

        public void UpdateButtonIcon(int buttonId, string iconPath, int size = 32)
        {
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
                System.Diagnostics.Debug.WriteLine($"更新按钮图标出错: {ex.Message}");
            }
        }

        public void UpdateTaskbarButtonIcon()
        {
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

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static IntPtr WindowSubclassProc(
            IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam,
            IntPtr uIdSubclass, IntPtr dwRefData)
        {
            if (dwRefData != IntPtr.Zero)
            {
                var handle = GCHandle.FromIntPtr(dwRefData);
                if (handle.Target is TaskbarHelper helper)
                    return helper.HandleWindowMessage(hWnd, uMsg, wParam, lParam);
            }
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private IntPtr HandleWindowMessage(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam)
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
            return DefSubclassProc(hWnd, uMsg, wParam, lParam);
        }

        private void HandleThumbButtonClick(int buttonId)
        {
            switch (buttonId)
            {
                case 0: _musicBrowseViewModel.LastMusicButton_Click(); break;
                case 1: _musicBrowseViewModel.PlayButton_Click(); break;
                case 2: _musicBrowseViewModel.NextMusicButton_Click(); break;
            }

            if (buttonId == 1)
            {
                string appDir = AppDomain.CurrentDomain.BaseDirectory;
                string newIconPath = AppData.IsPlaying
                    ? System.IO.Path.Combine(appDir, "Assets\\stop.ico")
                    : System.IO.Path.Combine(appDir, "Assets\\play.ico");
                UpdateButtonIcon(1, newIconPath);
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
                System.Diagnostics.Debug.WriteLine($"图片文件不存在: {imagePath}");
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
                        $"加载图片失败，错误码: {Marshal.GetLastWin32Error()}");
                    return LoadIcon(IntPtr.Zero, IDI_APPLICATION);
                }
                return hIcon;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"创建图标时出错: {ex.Message}");
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

            if (_isSubclassed && IsWindow(_hwnd))
            {
                RemoveWindowSubclass(_hwnd, &WindowSubclassProc, (IntPtr)1);
                _isSubclassed = false;
            }

            if (_selfHandle.IsAllocated)
                _selfHandle.Free();

            if (_pTaskbarList != IntPtr.Zero)
            {
                CallRelease(_pTaskbarList);
                _pTaskbarList = IntPtr.Zero;
            }

            ReleaseIcons();
            _nativeButtons = null;
            _isDisposed = true;
        }
    }
}