using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using testDemo.Taskbar;
using Windows.UI.ViewManagement;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : WinUIEx.WindowEx
    {
        public event EventHandler updateMusicList;
        public event EventHandler SettingLoaded;
        //public event EventHandler WindowClosed;
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        public event EventHandler updateSelectSection;
        public bool IsPlayingDetail = false;
        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private readonly INavigationService _navigationService;
        private int MinWindowWidth = 1280;
        private int MinWindowHeight = 720;
        public MainWindow()
        {
            InitializeComponent();
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetTitleBar(AppTitleBar);
            setWindow();
            AppData.m_hWnd = m_hwnd;
            this.Activated += MainWindow_Activated;
            ExtendsContentIntoTitleBar = true;
            // 在需要使用导航服务的地方获取工厂
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            // 为特定Frame创建导航服务
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            //this.Closed += MainWindow_Closed;
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            EfficiencyModeUtilities.SetEfficiencyMode(false);
            H.NotifyIcon.WindowExtensions.Hide(this, enableEfficiencyMode: false);
            H.NotifyIcon.WindowExtensions.Show(this, disableEfficiencyMode: true);
            if (AppSettings.IsProcessAboveNormal)
            {
                PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
            }
            this.AppWindow.Closing += AppWindow_Closing;
            //重复启动显示窗口
            newWndProcDelegate = new WindowHelper.WndProcDelegate(NewWindowProc);
            defaultWndProc = WindowHelper.GetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC);
            WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
            SaveMainWindowHandle(m_hwnd);
            uiSettings = new UISettings();
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        }

        private void setWindow()
        {
            //WindowSizeHelper.SetMinimumSize(m_hwnd, this, MinWindowWidth, MinWindowHeight);
            this.SetIcon("Assets/icon.ico");
            Title = ToolUtils.GetString("AppMainTitle");
            if (AppSettings.IsCustomAppSize)
            {
                this.CenterOnScreen(AppSettings.AppWidth, AppSettings.AppHeight);
                //WindowSizeHelper.ResizeWindowAndCenterInScreen(m_hwnd, AppSettings.AppHeight, AppSettings.AppWidth, this.AppWindow);
            }
            else
            {
                this.CenterOnScreen();
                //WindowSizeHelper.CenterInScreen(this.AppWindow);
            }
        }

        public void UpdateAppNotifyIconControl()
        {
            Debug.WriteLine(AppData.PlayMode);
            AppNotifyIconControl.UpdatePlayMode();
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
            });
        }
        //显示窗口
        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (msg == SingleInstanceHelper.WM_SHOWME)
                {
                    Debug.WriteLine("收到显示窗口消息");
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        if (this == null)
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


        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (AppSettings.isRunningBackend)
            {
                // 取消关闭操作
                args.Cancel = true;
                // 最小化到托盘
                this.Hide();
                PowerManagementHelper.DisableEfficiencyMode();
                if (AppSettings.IsProcessAboveNormal)
                {
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
                }
                else
                {
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.Normal);
                }
            }
        }


        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                var musicBrowsePage = App.Services.GetRequiredService<MusicBrowsePage>();
                musicBrowsePage?.ClosePage();
                AppNotifyIconControl?.Dispose();
                AppNotifyIconControl = null;
                if (m_hwnd != IntPtr.Zero && defaultWndProc != IntPtr.Zero)
                {
                    WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, defaultWndProc);
                    m_hwnd = IntPtr.Zero;
                    defaultWndProc = IntPtr.Zero;
                }
                _taskbarHelper?.Dispose();
                _taskbarHelper = null;
                //App.Current_Exit();
            }
            catch (Exception e)
            {
                Debug.WriteLine($"MainWindow 关闭时出错: {e.Message}");
                //App.Current_Exit();
            }finally { 
                args.Handled = false; 
            }
        }

        private async void InitializeApp()
        {
            try
            {
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();
                var tasks = new Task[] {
                        LoadMusicList(),
                        RefreshDevice(),
                        AutoScanFolder()
                };
                await Task.WhenAll(tasks);
                AppSettings.FontFamilyList = ToolUtils.GetSystemFontsInternal();
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                NavigateToDefaultPage();
                UpdateAppNotifyIconControl();
                updateSelectSection?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化错误: {ex.Message}");
            }
        }

        private async Task AutoScanFolder()
        {
            if (AppSettings.IsFolderWatchEnabled)
            {
                await AutoRescanService.AutoScan();
            }
        }

        public void UpdateTaskbarIcon()
        {
            _taskbarHelper?.UpdateTaskbarButtonIcon();
        }

        public void SetAppStyle()
        {
            themeStyleHelper.SetAppStyle();
        }

        public void SetAppTheme()
        {
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
        }

        private void NavigateToDefaultPage()
        {

            foreach (var item in NavigationViewControl.MenuItems)
            {
                if (item is NavigationViewItem navigationViewItem && navigationViewItem.Tag?.ToString() == AppSettings.DefualtEntry)
                {
                    NavigationViewControl.SelectedItem = navigationViewItem;
                    break;
                }
            }
            switch (AppSettings.DefualtEntry)
            {
                case "AddFolder":
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                    break;
                case "MusicBrowse":
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
                    break;
                default:
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            _navigationService.Navigate(typeof(SettingsPage), this, null, 100);
        }

        public void UpdateMusicList()
        {
            updateMusicList?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadMusicList(string search = null)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            await MusicDatabaseService.GetPlayListMusic();
        }

        public async Task RefreshDevice()
        {
            try
            {
                await ToolUtils.RefreshDevice();
                if (SettingLoaded != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SettingLoaded?.Invoke(this, EventArgs.Empty);
                    });
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated)
            {

                this.Activated -= MainWindow_Activated; // 只执行一次
            }
            if (_taskbarHelper == null)
            {
                InitializeTaskbarHelper();
            }
        }

        public void InitializeTaskbarHelper()
        {
            try
            {
                //IntPtr hwnd = WindowNative.GetWindowHandle(this);
                _taskbarHelper?.Dispose();
                _taskbarHelper = new TaskbarHelper(m_hwnd);
                _taskbarHelper.InitializeThumbButtons();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化任务栏助手出错: {ex.Message}");
            }
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                _navigationService.Navigate(typeof(SettingsPage), this, null, AppSettings.EntranceAnimationTime);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                        break;
                    case "MusicBrowse":
                        _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
                        break;
                }
            }
        }

        public void NavigateToMusicBrowsePage()
        {
            if (ContentFrame.Content is not MusicBrowsePage)
            {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                _navigationService.Navigate(typeof(MusicBrowsePage), null, null, 0);
            }
        }

        public void NavigationViewCollapsed()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavigationViewControlGrid.Opacity = 0;
            });
        }

        public void NavigationViewExpanded()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavigationViewControlGrid.Opacity = 1.0f;
            });
        }

        private void NavigationViewControl_BackRequested(NavigationView sender, NavigationViewBackRequestedEventArgs args)
        {
            App.Services.GetRequiredService<MusicBrowsePage>().BackButton();
        }

        public void DisableEnableBackButton(bool isEnable = false)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                NavigationViewControl.IsBackEnabled = isEnable;
            });
        }

        private void KeyboardAccelerator_Invoked(Microsoft.UI.Xaml.Input.KeyboardAccelerator sender, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs args)
        {
            NavigationViewControl_BackRequested(null, null);
        }

        public void AppTitleBarVisibility(bool isVisible)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                AppTitleBar.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
            });
        }

        private void NavigationViewControl_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            NavigationViewControlGrid.Opacity = 1.0f;
        }

        private void NavigationViewControl_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (IsPlayingDetail)
            {
                NavigationViewControlGrid.Opacity = 0;
            }
        }
    }
}
