using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using testDemo.Taskbar;
using Windows.UI.ViewManagement;
using WinRT.Interop;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using static WinUIMusicPlayer.Utils.ToolUtils;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
    {
        //public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public event EventHandler updateMusicList;
        public event EventHandler SettingLoaded;
        public event EventHandler WindowClosed;
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        //public event EventHandler playLastSong;
        //public event EventHandler playNextSong;
        //public event EventHandler playStop;
        public event EventHandler updateSelectSection;
        //public event EventHandler<PlayMode> changePlayMode;
        private Microsoft.UI.Windowing.AppWindow m_AppWindow;
        private TaskbarIcon notifyIcon;
        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        // 声明窗口句柄和消息处理相关变量
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private readonly INavigationService _navigationService;

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            ExtendsContentIntoTitleBar = true;
            // 在需要使用导航服务的地方获取工厂
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            // 为特定Frame创建导航服务
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            SetTitleBar(AppTitleBar);            
            this.Closed += MainWindow_Closed;
            m_AppWindow = ToolUtils.GetAppWindowForCurrentWindow(this);
            m_AppWindow.SetIcon("Assets/icon.ico");
            themeStyleHelper = new ThemeStyleHelper(this, m_AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            //if (AppNotifyIconControl != null)
            //{
                //AppNotifyIconControl.playLastSong += (s, e) => playLastSong?.Invoke(this, EventArgs.Empty);
                //AppNotifyIconControl.playStop += (s, e) => playStop?.Invoke(this, EventArgs.Empty);
                //AppNotifyIconControl.playNextSong += (s, e) => playNextSong?.Invoke(this, EventArgs.Empty);
                //AppNotifyIconControl.playModeEvent += (s, e) => changePlayMode?.Invoke(this, e);
            //}
            EfficiencyModeUtilities.SetEfficiencyMode(false);
            WindowExtensions.Hide(this, enableEfficiencyMode: false);
            WindowExtensions.Show(this, disableEfficiencyMode: true);
            m_AppWindow.Closing += AppWindow_Closing;
            // 获取窗口句柄并设置消息钩子
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            AppData.m_hWnd = m_hwnd;
            newWndProcDelegate = new WindowHelper.WndProcDelegate(NewWindowProc);
            defaultWndProc = WindowHelper.GetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC);
            WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
            Debug.WriteLine($"窗口句柄: {m_hwnd}");
            SaveMainWindowHandle(m_hwnd);
            uiSettings = new UISettings();
            // 注册颜色值变化事件，这会在系统主题变化时触发
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        }

        public void UpdateAppNotifyIconControl() {            
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
            Debug.WriteLine("colorchange");
            _ = DispatcherQueue.EnqueueAsync(() =>
            {
                // 重新应用样式
                SetAppStyle();
            });
        }

        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
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
                    }
                });
                return IntPtr.Zero;
            }

            // 调用默认窗口过程处理其他消息
            return WindowHelper.CallWindowProc(defaultWndProc, hWnd, msg, wParam, lParam);
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
                PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.Normal);
            }
        }


        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow closed.");
            if (m_hwnd != IntPtr.Zero && defaultWndProc != IntPtr.Zero)
            {
                WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, defaultWndProc);
            }
            _taskbarHelper?.Dispose();
            _taskbarHelper = null;
            WindowClosed?.Invoke(this, EventArgs.Empty);
            AppNotifyIconControl.ExitApplication();
            App.Current_Exit();
        }

        private async void InitializeApp()
        {
            try
            {
                //await MusicDatabaseService.Initialize();
                //await MusicDatabaseService.GetSettingsAsync();
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();
                var tasks = new Task[] {
                        //MusicDatabaseService.GetPlayStateAsync(),
                        //LoadFoldersAsync(),
                        LoadMusicList(),
                        RefreshDevice(),
                };
                await Task.WhenAll(tasks);
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                NavigateToDefaultPage();
                UpdateAppNotifyIconControl();
                updateSelectSection?.Invoke(this, EventArgs.Empty);
                //await AutoRescanService.AutoScan();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化错误: {ex.Message}");
            }
        }

        public void UpdateTaskbarIcon()
        {
            if (_taskbarHelper != null)
            {
                _taskbarHelper.UpdateTaskbarButtonIcon();
            }
        }

        //public void UpdateIconControl()
        //{
        //    if (AppNotifyIconControl != null)
        //    {
        //        AppNotifyIconControl.UpdatePlayNPause();
        //    }
        //}


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
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, 150);
                    //ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
                case "MusicBrowse":
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, 150);
                    //ContentFrame.Navigate(typeof(MusicBrowsePage));
                    break;
                default:
                    _navigationService.Navigate(typeof(AddFolderPage), null, null, 150);
                    //ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
            }
        }

        public void NavigateToSettingsPage()
        {
            NavigationViewControl.SelectedItem = NavigationViewControl.SettingsItem;
            _navigationService.Navigate(typeof(SettingsPage), this,null, 0);
            //ContentFrame.Navigate(typeof(SettingsPage), this);
        }

        //public async Task LoadFoldersAsync()
        //{
        //    LoadFolders?.Invoke(this, EventArgs.Empty);
        //}

        public void UpdateMusicList()
        {
            updateMusicList?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadMusicList(string search = null)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            _ = AlbumCoverService.LoadAlbumCoversInCacheAsync(AppData.allSongs);
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
                Title = ToolUtils.GetString("AppMainTitle");
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
                // 获取窗口句柄
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                _taskbarHelper?.Dispose();
                // 创建任务栏助手并初始化
                _taskbarHelper = new TaskbarHelper(hwnd);
                _taskbarHelper.InitializeThumbButtons();

                // 注册按钮点击事件
                //_taskbarHelper.ThumbButtonClicked += TaskbarHelper_ThumbButtonClicked;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化任务栏助手出错: {ex.Message}");
            }
        }

        //private void TaskbarHelper_ThumbButtonClicked(object sender, ThumbButtonClickedEventArgs e)
        //{
        //    if (e.ButtonId == 0)
        //    {
        //        playLastSong?.Invoke(this, EventArgs.Empty);
        //    }
        //    else if (e.ButtonId == 1)
        //    {
        //        playStop?.Invoke(this, EventArgs.Empty);
        //    }
        //    else if (e.ButtonId == 2)
        //    {
        //        playNextSong?.Invoke(this, EventArgs.Empty);
        //    }
        //}

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                _navigationService.Navigate(typeof(SettingsPage), this, null, 150);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        _navigationService.Navigate(typeof(AddFolderPage), null, null, 150);
                        break;
                    case "MusicBrowse":
                        _navigationService.Navigate(typeof(MusicBrowsePage), null, null, 150);
                        break;
                }
            }
        }

        public void NavigateToMusicBrowsePage()
        {
            if (!(ContentFrame.Content is MusicBrowsePage)) {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                _navigationService.Navigate(typeof(MusicBrowsePage),null,null,0);
            }            
        }
    }
}
