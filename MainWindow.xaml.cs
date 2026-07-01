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
using Windows.UI;
using Windows.UI.ViewManagement;
using Windows.UI.WindowManagement;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
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
            _logger = App.GetLogger<MainWindow>();
            this.Activated += MainWindow_Activated;
            themeStyleHelper = new ThemeStyleHelper(this, AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.CustomStyleChanged += (s, e) => customStyleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            this.AppWindow.Closing += AppWindow_Closing;
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

        private void SetWindow()
        {
            this.SetIcon("Assets/icon.ico");
            Title = ToolUtils.GetString("AppMainTitle");
            if (AppSettings.IsCustomAppSize)
            {
                this.CenterOnScreen(AppSettings.AppWidth, AppSettings.AppHeight);
            }
            else
            {
                this.CenterOnScreen();
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
                AppNotifyIconControl.Dispose();
                _taskbarHelper.Dispose();
            }
        }
    }
}
