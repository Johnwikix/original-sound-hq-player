using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.UI.ViewManagement;
using Windows.UI.WindowManagement;
using WinUIEx;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Taskbar;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.View.SubView;
using WinUIMusicPlayer.ViewModel;
using ZLinq;
using static ATL.LyricsInfo;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    public sealed partial class MainWindow : WindowEx,IDisposable
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
        public MainWindow()
        {
            InitializeComponent();
            AppData.HWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetWindow();
            ExtendsContentIntoTitleBar = true;            
            this.Activated += MainWindow_Activated;            
            themeStyleHelper = new ThemeStyleHelper(this, AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.CustomStyleChanged += (s, e) => customStyleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            EfficiencyModeUtilities.SetEfficiencyMode(false);
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
                Debug.WriteLine($"保存窗口句柄失败: {ex.Message}");
            }
        }
        private void UiSettings_ColorValuesChanged(UISettings sender, object args)
        {
            _ = DispatcherQueue.EnqueueAsync(() =>
            {
                SetAppStyle();
                if (AppSettings.AppTheme == "Default") {
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
                // 调用默认窗口过程处理其他消息
                return WindowHelper.CallWindowProc(defaultWndProc, hWnd, msg, wParam, lParam);
            }
            catch (Exception)
            {
                return IntPtr.Zero;
            }
        }


        private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (AppSettings.IsRunningBackend)
            {
                args.Cancel = true;
                this.Hide();
            }
            else
            {                
                await App.Current_Exit();
            }
        }

        public async void InitializeApp()
        {
            try
            {
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();               
                //ShellFrame.Content = App.Services.GetRequiredService<MainPage>();
                //App.Services.GetRequiredService<PlayingDetailPage>();
                //LoadingGrid.Visibility = Visibility.Collapsed;             
                //App.Services.GetRequiredService<AppViewModel>().IsInitialized = true;
            }
            catch (Exception)
            {
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
                    _taskbarHelper = new TaskbarHelper(AppData.HWnd);
                    _taskbarHelper.InitializeThumbButtons();
                }
                else
                {
                    _taskbarHelper.RecoverTaskbarHelper();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化任务栏助手出错: {ex.Message}");
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
