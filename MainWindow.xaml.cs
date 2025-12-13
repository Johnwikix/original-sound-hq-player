using H.NotifyIcon;
using H.NotifyIcon.EfficiencyMode;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Windows.UI.ViewManagement;
using WinUIEx;
using WinUIMusicPlayer.Extensions;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Services.NavigationService;
using WinUIMusicPlayer.Taskbar;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    public sealed partial class MainWindow : WinUIEx.WindowEx
    {
        public event EventHandler updateMusicList;
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        public event EventHandler customStyleChanged;
        public bool IsPlayingDetail = false;
        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private readonly INavigationService _navigationService;
        public MainWindow()
        {
            InitializeComponent();
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            SetTitleBar(AppTitleBar);
            setWindow();
            AppData.m_hWnd = m_hwnd;
            this.Activated += MainWindow_Activated;
            ExtendsContentIntoTitleBar = true;
            // 导航服务
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.ThemeChanged += (s, e) => themeChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.StyleChanged += (s, e) => styleChanged?.Invoke(this, EventArgs.Empty);
            themeStyleHelper.CustomStyleChanged += (s, e) => customStyleChanged?.Invoke(this, EventArgs.Empty);
            InitializeApp();
            EfficiencyModeUtilities.SetEfficiencyMode(false);
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


        private void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (AppSettings.isRunningBackend)
            {
                args.Cancel = true;
                this.Hide();
            }
            else
            {
                App.Current_Exit();
            }
        }

        private async void InitializeApp()
        {
            try
            {
                themeStyleHelper.SetAppStyle();
                themeStyleHelper.SetAppTheme();
                var longOpsTask = Task.Run(async () =>
                {
                    await InitialFileScan.InitialScan();
                    await LoadMusicList();
                });
                await Task.Delay(500);
                App.Services.GetRequiredService<IpcService>().Initializing();
                await Task.Delay(500);
                await Task.WhenAll(longOpsTask);
                AppSettings.FontFamilyList = ToolUtils.GetSystemFontsInternal();
                NavigateToDefaultPage();
                UpdateAppNotifyIconControl();
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
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

        public void SetCustomAppStyle()
        {
            themeStyleHelper.ChangeCustomAcrylicStyle();
        }

        public void SetAppTheme()
        {
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
                    _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
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

        public void RefreshDevice()
        {
            try
            {
                List<BassOutputDevice> BassOutputDevices = [];
                int n = BassWasapi.DeviceCount;
                for (int i = 0; i < n; i++)
                {
                    if (BassWasapi.GetDeviceInfo(i, out WasapiDeviceInfo deviceInfo))
                    {
                        if (deviceInfo.IsEnabled && deviceInfo.Type != WasapiDeviceType.Microphone)
                        {
                            if (!BassOutputDevices.AsValueEnumerable().Any(d => d.Name == deviceInfo.Name))
                            {
                                Debug.WriteLine($"{deviceInfo.Name}: {i}");
                                BassOutputDevices.Add(new BassOutputDevice
                                {
                                    Name = deviceInfo.Name,
                                    Id = i
                                });
                            }
                        }
                    }
                }
                var device = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == AppSettings.DeviceName);
                if (device is null)
                {
                    AppSettings.DeviceName = ToolUtils.GetString("DefaultDevice");
                    AppSettings.BassOutputDeviceId = -1;
                }
                else
                {
                    AppSettings.DeviceName = device.Name;
                    AppSettings.BassOutputDeviceId = device.Id;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"刷新音频设备失败: {ex.Message}");
            }
        }

        private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
        {
            if (AppSettings.IsUpdateBackDrop)
            {
                themeStyleHelper?.IsBackdropActive(args.WindowActivationState != WindowActivationState.Deactivated);
            }
            if (_taskbarHelper is null)
            {
                InitializeTaskbarHelper();
            }
        }

        public void InitializeTaskbarHelper()
        {
            try
            {
                if (_taskbarHelper is null)
                {
                    _taskbarHelper = new TaskbarHelper(m_hwnd);
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
            if (IsPlayingDetail && ContentFrame.Content.GetType() == typeof(MusicBrowsePage))
            {
                NavigationViewControlGrid.Opacity = 0;
            }
        }
    }
}
