using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using testDemo.Taskbar;
using Windows.Graphics;
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
        //private Microsoft.UI.Windowing.AppWindow m_AppWindow;
        //private TaskbarIcon notifyIcon;
        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        // 声明窗口句柄和消息处理相关变量
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private readonly INavigationService _navigationService;
        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);
        private double scaleFactor = 1f;
        private int MinWindowWidth = 1280;
        private int MinWindowHeight = 720;
        public MainWindow()
        {
            InitializeComponent();
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            this.Activated += MainWindow_Activated;
            CenterWindow();
            ExtendsContentIntoTitleBar = true;
            // 在需要使用导航服务的地方获取工厂
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            // 为特定Frame创建导航服务
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            SetTitleBar(AppTitleBar);
            //this.AppWindow.Changed += AppWindow_Changed;
            this.Closed += MainWindow_Closed;
            this.AppWindow.SetIcon("Assets/icon.ico");
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            EfficiencyModeUtilities.SetEfficiencyMode(false);
            H.NotifyIcon.WindowExtensions.Hide(this, enableEfficiencyMode: false);
            H.NotifyIcon.WindowExtensions.Show(this, disableEfficiencyMode: true);
            if (AppSettings.IsProcessAboveNormal) {
                PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
            }
            this.AppWindow.Closing += AppWindow_Closing;            
            AppData.m_hWnd = m_hwnd;
            uint dpi = GetDpiForWindow(m_hwnd);
            scaleFactor = dpi / 96.0;
            MinWindowWidth = (int)(MinWindowWidth * scaleFactor);
            MinWindowHeight = (int)(MinWindowHeight * scaleFactor);
            WindowSizeHelper.SetMinimumSize(m_hwnd,this, MinWindowWidth, MinWindowHeight);
            newWndProcDelegate = new WindowHelper.WndProcDelegate(NewWindowProc);
            defaultWndProc = WindowHelper.GetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC);
            WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
            Debug.WriteLine($"窗口句柄: {m_hwnd}");
            SaveMainWindowHandle(m_hwnd);
            uiSettings = new UISettings();
            // 注册颜色值变化事件，这会在系统主题变化时触发
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
        }

        private void CenterWindow()
        {
            var displayArea = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
            var workArea = displayArea.WorkArea;
            var windowSize = this.AppWindow.Size;
            var x = (workArea.Width - windowSize.Width) / 2 + workArea.X;
            var y = (workArea.Height - windowSize.Height) / 2 + workArea.Y;
            this.AppWindow.Move(new PointInt32(x, y));
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
                if (AppSettings.IsProcessAboveNormal)
                {
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
                }
                else {
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.Normal);
                }                
            }
        }


        private async void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            try
            {
                
                var tasks = new Task[] {
                    App.Services.GetRequiredService<MusicBrowsePage>().ClosePage()
                };
                AppNotifyIconControl?.Dispose();            
                if (m_hwnd != IntPtr.Zero && defaultWndProc != IntPtr.Zero)
                {
                    WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, defaultWndProc);
                }
                _taskbarHelper?.Dispose();
                _taskbarHelper = null;                
                await Task.WhenAll(tasks);
                WindowClosed?.Invoke(this, EventArgs.Empty);
                App.Current_Exit();
            }
            catch (Exception e) {
                Debug.WriteLine($"MainWindow 关闭时出错: {e.Message}");
            }                 
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
            _navigationService.Navigate(typeof(SettingsPage), this,null, 100);
        }

        public void UpdateMusicList()
        {
            updateMusicList?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadMusicList(string search = null)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            //_ = AlbumCoverService.LoadAlbumCoversInCacheAsync(AppData.allSongs);
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
                // 获取窗口句柄
                IntPtr hwnd = WindowNative.GetWindowHandle(this);
                _taskbarHelper?.Dispose();
                // 创建任务栏助手并初始化
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
            if (ContentFrame.Content is not MusicBrowsePage) {
                NavigationViewControl.SelectedItem = NavigationViewControl.MenuItems[1];
                _navigationService.Navigate(typeof(MusicBrowsePage),null,null,0);
            }            
        }
    }
}
