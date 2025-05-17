using CommunityToolkit.Mvvm.Messaging;
using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.ApplicationModel.Resources;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;
using Windows.UI.ViewManagement;
using WinUIMusicPlayer.Controls;
using WinUIMusicPlayer.Handler;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using static System.Windows.Forms.VisualStyles.VisualStyleElement;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Microsoft.UI.Xaml.Window
    {
        public event EventHandler<IEnumerable<Folder>> FoldersLoaded;
        public event EventHandler updateMusicList;
        public event EventHandler SettingLoaded;
        public event EventHandler WindowClosed;
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        public event EventHandler playLastSong;
        public event EventHandler playNextSong;
        public event EventHandler playStop;
        private Microsoft.UI.Windowing.AppWindow m_AppWindow;
        private TaskbarIcon notifyIcon;
        private ThemeStyleHelper themeStyleHelper;
        private NotifyIconHelper notifyIconHelper;
        private UISettings uiSettings;
        private WindowMessageHandler _messageHandler;
        // 声明窗口句柄和消息处理相关变量
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeApp();
            this.Closed += MainWindow_Closed;
            m_AppWindow = ToolUtils.GetAppWindowForCurrentWindow(this);
            m_AppWindow.SetIcon("Assets/icon.ico");
            themeStyleHelper = new ThemeStyleHelper(this, m_AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);

            // 初始化系统托盘图标辅助类
            //notifyIconHelper = new NotifyIconHelper(this, m_AppWindow);
            //notifyIconHelper.PlayLastSong += (s, e) => playLastSong?.Invoke(this, EventArgs.Empty);
            //notifyIconHelper.PlayNextSong += (s, e) => playNextSong?.Invoke(this, EventArgs.Empty);
            //notifyIconHelper.PlayStop += (s, e) => playStop?.Invoke(this, EventArgs.Empty);
            //notifyIconHelper.Initialize();
            //InitializeNotifyIcon();
            if (AppNotifyIconControl != null)
            {
                AppNotifyIconControl.playLastSong += (s, e) => playLastSong?.Invoke(this, EventArgs.Empty);
                AppNotifyIconControl.playStop += (s, e) => playStop?.Invoke(this, EventArgs.Empty);
                AppNotifyIconControl.playNextSong += (s, e) => playNextSong?.Invoke(this, EventArgs.Empty);
            }
            EfficiencyModeUtilities.SetEfficiencyMode(false);
            //PowerManagementHelper.DisableEfficiencyMode();
            //PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.Normal);
            m_AppWindow.Closing += AppWindow_Closing;
            // 获取窗口句柄并设置消息钩子
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            newWndProcDelegate = new WindowHelper.WndProcDelegate(NewWindowProc);
            defaultWndProc = WindowHelper.GetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC);
            WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
            uiSettings = new UISettings();
            // 注册颜色值变化事件，这会在系统主题变化时触发
            uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
            // 设置窗口标识符，使其他实例可以找到它
            SingleInstanceHelper.SetWindowIdentifier(m_hwnd);
            //_messageHandler = new WindowMessageHandler(m_hwnd);
            //_messageHandler.MessageReceived += OnMessageReceived;
        }

        private void OnMessageReceived(object? sender, WindowMessageEventArgs e)
        {
            if (e.MessageId == SingleInstanceHelper.WM_SHOWME)
            {
                // 在 UI 线程上执行
                DispatcherQueue.TryEnqueue(() =>
                {
                    // 显示并激活窗口
                    if (!this.Visible)
                    {
                        this.Show();
                    }
                    this.Activate();
                });
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
            // 处理自定义的显示消息
            if (msg == SingleInstanceHelper.WM_SHOWME)
            {
                Debug.WriteLine("收到显示窗口消息");
                // 在UI线程上执行显示窗口
                DispatcherQueue.TryEnqueue(() =>
                {
                    ShowWindow();
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
                m_AppWindow.Hide();
            }
        }


        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow closed.");
            if (m_hwnd != IntPtr.Zero && defaultWndProc != IntPtr.Zero)
            {
                WindowHelper.SetWindowLongPtr(m_hwnd, WindowHelper.GWLP_WNDPROC, defaultWndProc);
            }
            WindowClosed?.Invoke(this, EventArgs.Empty);
        }

        private async void InitializeApp()
        {
            try
            {
                await MusicDatabaseService.Initialize();
                await MusicDatabaseService.GetSettingsAsync();
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();
                var tasks = new Task[] {
                        MusicDatabaseService.GetPlayStateAsync(),
                        LoadFoldersAsync(),
                        LoadMusicList(),
                        RefreshDevice(),
                };
                await Task.WhenAll(tasks);
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                NavigateToDefaultPage();
                //await AutoRescanService.AutoScan();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化错误: {ex.Message}");
            }
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

        public void ShowWindow()
        {
            notifyIconHelper.ShowWindow();
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
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
                case "MusicBrowse":
                    ContentFrame.Navigate(typeof(MusicBrowsePage));
                    break;
                default:
                    ContentFrame.Navigate(typeof(AddFolderPage));
                    break;
            }
        }
        public async Task LoadFoldersAsync()
        {
            var folderList = await MusicDatabaseService.GetFoldersAsync();
            FoldersLoaded?.Invoke(this, folderList);
        }

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
                string selector = MediaDevice.GetAudioRenderSelector();
                DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
                AppSettings.outputDeviceList.Clear();
                foreach (DeviceInformation device in devices)
                {
                    AppSettings.outputDeviceList.Add(device.Name);
                }
                if (SettingLoaded != null)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        SettingLoaded?.Invoke(this, EventArgs.Empty);
                    });
                }
                GC.Collect();
                GC.WaitForPendingFinalizers();
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
        }

        private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(SettingsPage), this);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        ContentFrame.Navigate(typeof(AddFolderPage));
                        break;
                    case "MusicBrowse":
                        ContentFrame.Navigate(typeof(MusicBrowsePage));
                        break;
                }
            }
        }
    }
}
