using CommunityToolkit.WinUI;
using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using System;
using System.Diagnostics;
using System.Timers;
using Windows.Graphics;
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.WindowManagement;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Taskbar;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    public sealed partial class MainWindow : WindowEx, IDisposable
    {
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        public event EventHandler customStyleChanged;
        public event EventHandler<bool> backdropInputState;

        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private ILogger<MainWindow> _logger;
        private readonly object _trimLock = new();

        public MainWindow()
        {
            InitializeComponent();
            AppData.HWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindow();
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
            if (AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            }
            _logger = App.GetLogger<MainWindow>();
            this.Activated += MainWindow_Activated;
            themeStyleHelper = new ThemeStyleHelper(this, AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.CustomStyleChanged += (s, e) => customStyleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            this.AppWindow.Closing += AppWindow_Closing;
            this.AppWindow.Changed += AppWindow_Changed;
            //重复启动显示窗口
            newWndProcDelegate = new WindowHelper.WndProcDelegate(NewWindowProc);
            defaultWndProc = WindowHelper.GetWindowLongPtr(AppData.HWnd, WindowHelper.GWLP_WNDPROC);
            WindowHelper.SetWindowLongPtr(AppData.HWnd, WindowHelper.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
            SaveMainWindowHandle(AppData.HWnd);
            uiSettings = new UISettings();
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            InitializeTaskbarHelper();
        }

        // 仅在 OverlappedPresenterState.Restored(普通窗口)时刷新此基准；
        // Maximized / Minimized / FullScreen 等瞬时状态不会覆盖本字段——
        // 退出保存时一律回退到此处，作为下次启动的还原依据。
        // 启动路径 SetWindow() 在 MoveAndResize/CenterOnScreen 之后会立即抓一次，
        // 保证首次退出也能拿到有效基准。
        // 写入前还要通过尺寸合理性校验，防止状态切换瞬间捕获到全屏/最大化尺寸。
        private (int X, int Y, int Width, int Height) _lastRestoredBounds;
        private bool _hasRestoredBounds;
        public (int X, int Y, int Width, int Height) TrackedBounds => _lastRestoredBounds;
        public bool HasTrackedBounds => _hasRestoredBounds;
        public bool IsCurrentlyMaximized => WindowSizeHelper.IsAppWindowMaximized(AppWindow);

        // 窗口尺寸合理范围: 200..10000。多屏 + 高 DPI 也不可能超过此范围。
        // 任何超出该范围的捕获值都被视为污染(如全屏/最大化尺寸), 拒绝写入。
        private const int MinReasonableSize = 200;
        private const int MaxReasonableSize = 10000;

        private void CaptureRestoredBounds()
        {
            if (AppWindow == null) return;
            if (AppWindow.Presenter is not OverlappedPresenter op) return;
            // 仅信任普通窗口的位置/尺寸；其他状态由 _lastRestoredBounds 兜底。
            if (op.State != OverlappedPresenterState.Restored) return;

            var pos = AppWindow.Position;
            var size = AppWindow.Size;

            // 纵深防御: 即使 DidPresenterChange 守卫因未来 API 变更失效,
            // 也不写入超大/超小尺寸。校验失败保留旧值, 不覆盖 _lastRestoredBounds。
            if (size.Width < MinReasonableSize || size.Width > MaxReasonableSize) return;
            if (size.Height < MinReasonableSize || size.Height > MaxReasonableSize) return;

            _lastRestoredBounds = (pos.X, pos.Y, size.Width, size.Height);
            _hasRestoredBounds = true;
        }

        private void AppWindow_Changed(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowChangedEventArgs args)
        {
            if (AppWindow == null) return;

            // 状态切换瞬间 DidPositionChange/DidSizeChange 会先于 DidPresenterChange 到达,
            // 此时 op.State 仍是旧值(Restored), 但 AppWindow.Position/Size 已经是新状态
            // (Maximized/FullScreen/Minimized)的尺寸. 一旦捕获就会污染 _lastRestoredBounds.
            // 故状态切换期间绝对不捕获 — 根因防御.
            if (args.DidPresenterChange) return;

            if (args.DidPositionChange || args.DidSizeChange)
            {
                // 注意: 窗口进入 FullScreen 时 Presenter 类型会从 OverlappedPresenter 切换到
                // FullScreenPresenter (二者是 AppWindowPresenter 的兄弟类, 不是父子),
                // 此时 `is OverlappedPresenter` 永远为 false 是预期行为 — FullScreen 下
                // _lastRestoredBounds 不应被刷新 (配合尺寸合理性校验 200..10000 双重防御)。
                if (AppWindow.Presenter is OverlappedPresenter op
                    && op.State == OverlappedPresenterState.Restored)
                {
                    CaptureRestoredBounds();
                }
            }
        }

        private void SetWindow()
        {
            this.SetIcon("Assets/icon.ico");
            Title = ToolUtils.GetString("AppMainTitle");

            var ps = App.Services.GetRequiredService<MusicDatabaseService>().CurrentPlayState;
            if (ps != null && ps.HasWindowBounds
                && WindowSizeHelper.IsBoundsOnScreen(ps.WindowX, ps.WindowY, ps.WindowWidth, ps.WindowHeight))
            {
                AppWindow.MoveAndResize(new RectInt32(ps.WindowX, ps.WindowY, ps.WindowWidth, ps.WindowHeight));
            }
            else
            {
                this.CenterOnScreen();
            }

            CaptureRestoredBounds();

            if (ps != null && ps.IsMaximized
                && AppWindow.Presenter is OverlappedPresenter overlapped)
            {
                overlapped.Maximize();
            }
        }

        private void SaveMainWindowHandle(IntPtr handle)
        {
            try
            {
                //使用注册表
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\SennpeiStudio\OriginalSoundHIFIPlayer"))
                {
                    key.SetValue("MainWindowHandle", handle.ToInt64());
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"保存窗口句柄失败: {ex.Message}");
            }
        }
        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            _ = DispatcherQueue.EnqueueAsync(() =>
            {
                SetAppStyle();
                if (AppSettings.AppTheme == "Default")
                {
                    App.Services.GetRequiredService<MusicBrowseViewModel>().ThemeChangedUpdateCover();
                }
            });
        }
        //显示窗口
        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == SingleInstanceHelper.WM_SHOWME)
                {
                    //Debug.WriteLine("收到显示窗口消息");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (this is null)
                        {
                            return;
                        }

                        if (!this.Visible)
                        {
                            this.Show();
                            InitializeTaskbarHelper();
                        }
                    });
                    return IntPtr.Zero;
                }
                if (msg == 0x0312) // WM_HOTKEY
                {
                    int id = (int)wParam;
                    Helper.GlobalHotKeyHook.TryInvokeAction(id);
                    return IntPtr.Zero;
                }
                // 调用默认窗口过程处理其他消息
                return WindowHelper.CallWindowProc(defaultWndProc, hWnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "处理窗口消息时发生错误");
                return IntPtr.Zero;
            }
        }


        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, AppWindowClosingEventArgs args)
        {
            AppWindow.Changed -= AppWindow_Changed;
            if (AppSettings.IsRunningBackend)
            {
                args.Cancel = true;
                this.Hide();
                if (AppSettings.IsTrimOnHideEnabled)
                    _ = WorkingSetCompressor.TrimSelfAsync();
            }
            else
            {
                await App.Current_Exit();
            }
        }

        public void InitializeApp()
        {
            try
            {
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "应用主题初始化失败，可能是因为系统主题设置不受支持。");
            }
        }

        public void ShowMainPage()
        {
            ShellFrame.Content = App.Services.GetRequiredService<MainPage>();
            LoadingGrid.Visibility = Visibility.Collapsed;
        }

        public void UpdateTaskbarIcon()
        {
            _taskbarHelper?.UpdateTaskbarButtonIcon();
        }

        public void SetAppStyle()
        {
            themeStyleHelper.SetAppStyle();
        }

        public void SetCustomAppStyle()
        {
            themeStyleHelper.ChangeCustomAcrylicStyle();
        }

        public void SetAppTheme()
        {
            themeStyleHelper.SetAppTheme();
        }

        public void UpdateBackdropActiveState(bool isActive)
        {
            themeStyleHelper.UpdateBackdropActiveState(isActive);
            backdropInputState?.Invoke(this, isActive);
        }



        public void InitializeTaskbarHelper()
        {
            try
            {
                if (_taskbarHelper is null)
                {
                    _taskbarHelper = new TaskbarHelper(AppData.HWnd, App.Services.GetRequiredService<MusicBrowseViewModel>());
                    _taskbarHelper.ErrorOccurred += (_, e) =>
                    {
                        _logger.LogError(e.Exception, "任务栏助手发生错误");
                    };
                    _taskbarHelper.InitializeThumbButtons();
                }
                else
                {
                    _taskbarHelper.RecoverTaskbarHelper();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化任务栏助手失败");
            }
        }
        public void ToggleShowHide()
        {
            if (this.Visible)
            {
                this.Hide();
                if (AppSettings.IsTrimOnHideEnabled)
                    _ = WorkingSetCompressor.TrimSelfAsync();
            }
            else
            {
                this.Show();
                InitializeTaskbarHelper();
                this.Activate();
                WindowHelper.SetForegroundWindow(AppData.HWnd);
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool dispose)
        {
            if (dispose)
            {
                AppNotifyIconControl?.Dispose();
                _taskbarHelper?.Dispose();
            }
        }
    }
}
