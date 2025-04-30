using H.NotifyIcon;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NAudio.CoreAudioApi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.UI.ViewManagement;
using Windows.UI.WindowManagement;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
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
        public MainWindow current;
        private Microsoft.UI.Windowing.AppWindow m_AppWindow;
        private TaskbarIcon notifyIcon;

        // 声明窗口句柄和消息处理相关变量
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WndProcDelegate newWndProcDelegate;

        // 窗口过程委托和常量定义
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const int GWLP_WNDPROC = -4;

        [DllImport("user32.dll")]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        private const int SW_RESTORE = 9;

        public MainWindow()
        {
            InitializeComponent();
            this.Activated += MainWindow_Activated;            
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
            InitializeApp();
            this.Closed += MainWindow_Closed;
            current = this;            
            m_AppWindow = GetAppWindowForCurrentWindow(this);
            m_AppWindow.SetIcon("Assets/icon.ico");
            InitializeNotifyIcon();
            PowerManagementHelper.DisableEfficiencyMode();
            m_AppWindow.Closing += AppWindow_Closing;

            // 获取窗口句柄并设置消息钩子
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            newWndProcDelegate = new WndProcDelegate(NewWindowProc);
            defaultWndProc = GetWindowLongPtr(m_hwnd, GWLP_WNDPROC);
            SetWindowLongPtr(m_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(newWndProcDelegate));
        }       

        private IntPtr NewWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            // 处理自定义的显示消息
            if (msg == App.WM_SHOWME)
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
            return CallWindowProc(defaultWndProc, hWnd, msg, wParam, lParam);
        }

        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (AppSettings.isRunningBackend) {
                // 取消关闭操作
                args.Cancel = true;
                // 最小化到托盘
                m_AppWindow.Hide();
            }
        }



        private Microsoft.UI.Windowing.AppWindow GetAppWindowForCurrentWindow(Window window)
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(windowId);
        }


        private void MainWindow_Closed(object sender, WindowEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine("MainWindow closed.");
            if (m_hwnd != IntPtr.Zero && defaultWndProc != IntPtr.Zero)
            {
                SetWindowLongPtr(m_hwnd, GWLP_WNDPROC, defaultWndProc);
            }
            WindowClosed?.Invoke(this, EventArgs.Empty);
        }

        private async void InitializeApp()
        {
            try
            {                
                await MusicDatabaseService.Initialize();
                await LoadDeviceState();
                SetAppStyle();
                SetAppTheme();
                var tasks = new Task[] {
                        LoadAppState(),
                        LoadFoldersAsync(),
                        LoadMusicList(),
                        RefreshDevice(),
                        //LoadPlayList()
                };
                await Task.WhenAll(tasks);                
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                NavigateToDefaultPage();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"初始化错误: {ex.Message}");
            }
        }

        public void SetAppStyle() {
            try {
                switch (AppSettings.AppStyle)
                {
                    case "Acrylic":
                        SystemBackdrop = new DesktopAcrylicBackdrop();
                        break;
                    case "Mica":
                        SystemBackdrop = new MicaBackdrop();
                        break;
                    default:
                        SystemBackdrop = new DesktopAcrylicBackdrop();
                        break;
                }
                styleChanged?.Invoke(this, EventArgs.Empty);
            } catch(Exception ex) {
                Debug.WriteLine(ex.Message);
            }
        }

        public void SetAppTheme() {
            try {
                Microsoft.UI.Windowing.AppWindowTitleBar m_TitleBar = m_AppWindow.TitleBar;
                if (current.Content is FrameworkElement rootElement)
                {
                    switch (AppSettings.AppTheme)
                    {
                        case "Default":
                            m_TitleBar.ButtonForegroundColor = null;
                            m_TitleBar.ButtonHoverForegroundColor = null;
                            m_TitleBar.ButtonPressedForegroundColor = null;
                            m_TitleBar.ButtonHoverBackgroundColor = null;
                            m_TitleBar.ButtonPressedBackgroundColor = null;
                            rootElement.RequestedTheme = ElementTheme.Default;
                            AppSettings.elementTheme = ElementTheme.Default;
                            break;
                        case "Dark":
                            rootElement.RequestedTheme = ElementTheme.Dark;
                            AppSettings.elementTheme = ElementTheme.Dark;
                            m_TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                            m_TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                            m_TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                            m_TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 50, 50, 50);
                            m_TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 80, 80, 80);
                            break;
                        case "Light":
                            rootElement.RequestedTheme = ElementTheme.Light;
                            AppSettings.elementTheme = ElementTheme.Light;
                            m_TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                            m_TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                            m_TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                            m_TitleBar.ButtonHoverBackgroundColor = Windows.UI.Color.FromArgb(255, 220, 220, 220);
                            m_TitleBar.ButtonPressedBackgroundColor = Windows.UI.Color.FromArgb(255, 190, 190, 190);
                            break;
                        default:
                            m_TitleBar.ButtonForegroundColor = null;
                            m_TitleBar.ButtonHoverForegroundColor = null;
                            m_TitleBar.ButtonPressedForegroundColor = null;
                            m_TitleBar.ButtonHoverBackgroundColor = null;
                            m_TitleBar.ButtonPressedBackgroundColor = null;
                            rootElement.RequestedTheme = ElementTheme.Default;
                            AppSettings.elementTheme = ElementTheme.Default;
                            break;
                    }
                    themeChanged?.Invoke(this, EventArgs.Empty);
                }
            } catch(Exception ex) {
                Debug.WriteLine(ex.Message);
            }
        }

        private void InitializeNotifyIcon()
        {
            try
            {
                notifyIcon = new TaskbarIcon();
                notifyIcon.IconSource = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/icon.ico"));
                notifyIcon.ToolTipText = "原音HIFI";
                notifyIcon.Visibility = Visibility.Visible;
                var contextMenu = new MenuFlyout();
                var openCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                openCommand.Label = "显示";
                openCommand.ExecuteRequested += (s, e) => {
                    ShowWindow();
                };                
                var lastCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                lastCommand.Label = "上一首";
                lastCommand.ExecuteRequested += (s, e) => {
                    LastSong();
                };

                var playCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                playCommand.Label = "播放/暂停";
                playCommand.ExecuteRequested += (s, e) => {
                    PlayStop();
                };
                var nextCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                nextCommand.Label = "下一首";
                nextCommand.ExecuteRequested += (s, e) => {
                    NextSong();
                };
                var exitCommand = new Microsoft.UI.Xaml.Input.XamlUICommand();
                exitCommand.Label = "退出";
                exitCommand.ExecuteRequested += (s, e) => {
                    CloseApplication();
                };
                var openMenuItem = new MenuFlyoutItem { Text = "显示", Command = openCommand };
                contextMenu.Items.Add(openMenuItem);
                var lastMenuItem = new MenuFlyoutItem { Text = "上一首", Command = lastCommand };
                contextMenu.Items.Add(lastMenuItem);
                var playMenuItem = new MenuFlyoutItem { Text = "播放/暂停", Command = playCommand };
                contextMenu.Items.Add(playMenuItem);
                var nextMenuItem = new MenuFlyoutItem { Text = "下一首", Command = nextCommand };
                contextMenu.Items.Add(nextMenuItem);
                contextMenu.Items.Add(new MenuFlyoutSeparator());
                var exitMenuItem = new MenuFlyoutItem { Text = "退出", Command = exitCommand };
                contextMenu.Items.Add(exitMenuItem);
                notifyIcon.ContextFlyout = contextMenu;
                notifyIcon.DoubleClickCommand = new RelayCommand(() =>
                {
                    ShowWindow();
                });

                if (this.Content is FrameworkElement rootElement)
                {
                    rootElement.Resources.Add("NotifyIconResource", notifyIcon);
                }

                notifyIcon.ForceCreate();
                notifyIcon.ShowNotification("原音HIFI", "托盘图标已初始化");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"初始化系统托盘图标时出错: {ex.Message}");
            }
        }

        private void NextSong()
        {
            playLastSong?.Invoke(this, EventArgs.Empty);
        }

        private void PlayStop()
        {
            playStop?.Invoke(this, EventArgs.Empty);
        }

        private void LastSong()
        {
            playLastSong?.Invoke(this, EventArgs.Empty);
        }

        public void ShowWindow()
        {
            Debug.WriteLine("准备显示窗口");
            DispatcherQueue.TryEnqueue(() =>
            {
                if (m_AppWindow != null)
                {
                    // 先确保窗口显示
                    m_AppWindow.Show();

                    // 设置前台窗口激活
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    SetForegroundWindow(hwnd);

                    // 如果窗口被最小化，则恢复它
                    if (IsIconic(hwnd))
                    {
                        ShowWindow(hwnd, SW_RESTORE);
                    }

                    Debug.WriteLine("窗口已显示并激活");
                }
                else
                {
                    Debug.WriteLine("m_AppWindow为空");
                }
            });
        }
        private void CloseApplication()
        {
            m_AppWindow.Closing -= AppWindow_Closing;
            notifyIcon?.Dispose();
            this.Close();
        }

        public class RelayCommand : System.Windows.Input.ICommand
        {
            private readonly Action _execute;
            private readonly Func<bool> _canExecute;

            public RelayCommand(Action execute, Func<bool> canExecute = null)
            {
                _execute = execute ?? throw new ArgumentNullException(nameof(execute));
                _canExecute = canExecute;
            }

            public event EventHandler CanExecuteChanged;

            public bool CanExecute(object parameter) => _canExecute?.Invoke() ?? true;

            public void Execute(object parameter) => _execute();

            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
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

        public void UpdateMusicList() {
            updateMusicList?.Invoke(this, EventArgs.Empty);
        }

        public async Task LoadMusicList(string search = null)
        {
            AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
            _ = AlbumCoverService.LoadAlbumCoversInCacheAsync(AppData.allSongs);
            await MusicDatabaseService.GetPlayListMusic();
            //MusicListLoaded?.Invoke(this, musicList);
        }

        public async Task RefreshDevice()
        {
            try
            {
                if (!AppSettings.isDsd)
                {
                    await Task.Run(() =>
                    {
                        using (MMDeviceEnumerator enumerator = new MMDeviceEnumerator())
                        {
                            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                            AppSettings.outputDeviceList.Clear();
                            foreach (var device in devices)
                            {
                                AppSettings.outputDeviceList.Add(device.FriendlyName);
                            }
                            // 回到 UI 线程触发事件
                            if (SettingLoaded != null)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    SettingLoaded?.Invoke(this, EventArgs.Empty);
                                });
                            }
                        }
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                    });
                }
                else
                {
                    using (var csCoreEnumerator = new CSCore.CoreAudioAPI.MMDeviceEnumerator())
                    {
                        using (var devices = csCoreEnumerator.EnumAudioEndpoints(CSCore.CoreAudioAPI.DataFlow.Render, CSCore.CoreAudioAPI.DeviceState.Active))
                        {
                            AppSettings.outputDeviceList.Clear();
                            foreach (var device in devices)
                            {
                                AppSettings.outputDeviceList.Add(device.FriendlyName);
                            }
                            // 回到 UI 线程触发事件
                            if (SettingLoaded != null)
                            {
                                DispatcherQueue.TryEnqueue(() =>
                                {
                                    SettingLoaded?.Invoke(this, EventArgs.Empty);
                                });
                            }
                        }
                    }
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
            }
        }

        private async Task LoadAppState()
        {
            var playState = await MusicDatabaseService.GetPlayStateAsync();
            AppData.PlayMode = playState.PlayMode;
            AppData.LastPlayedMusicId = playState.LastPlayedMusicId;
            AppData.Volume = playState.Volume;
        }
        public async Task LoadDeviceState()
        {
            await MusicDatabaseService.GetSettingsAsync();
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            this.Activated -= MainWindow_Activated;
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
