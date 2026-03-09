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

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer
{
    public sealed partial class MainWindow : WinUIEx.WindowEx, INotifyPropertyChanged
    {
        //public event EventHandler updateMusicList;
        public event EventHandler themeChanged;
        public event EventHandler styleChanged;
        public event EventHandler customStyleChanged;
        public event EventHandler<bool> backdropInputState;
        public event PropertyChangedEventHandler? PropertyChanged;
        public EqualizerDialog EqualizerDialog { get; set; }

        private ThemeStyleHelper themeStyleHelper;
        private UISettings uiSettings;
        private IntPtr m_hwnd;
        private IntPtr defaultWndProc;
        private WindowHelper.WndProcDelegate newWndProcDelegate;
        private TaskbarHelper _taskbarHelper;
        private readonly INavigationService _navigationService;
        private readonly INavigationService _playingNavigation;

        public bool IsBackBtnEnable
        {
            get;
            set
            {
                if (field != value)
                {
                    field = value;
                    OnPropertyChanged();
                }
            }
        } = true;
        private MusicDatabaseService MusicDatabaseService { get; }
        public MainWindow()
        {
            MusicDatabaseService = App.Services.GetRequiredService<MusicDatabaseService>();
            InitializeComponent();
            m_hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            //SetTitleBar(AppTitleBar);
            SetWindow();
            AppData.m_hWnd = m_hwnd;
            this.Activated += MainWindow_Activated;
            ExtendsContentIntoTitleBar = true;
            // 导航服务
            var navigationServiceFactory = App.Services.GetRequiredService<INavigationServiceFactory>();
            _navigationService = navigationServiceFactory.CreateNavigationService(ContentFrame);            
            _navigationService.RegisterPage<AddFolderPage>();
            _navigationService.RegisterPage<MusicBrowsePage>();
            _navigationService.RegisterPage<SettingsPage>();
            _playingNavigation = navigationServiceFactory.CreateNavigationService(PlayingFrame);
            _playingNavigation.RegisterPage<PlayingDetailPage>();
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
            EqualizerDialog = new EqualizerDialog();
            EqualizerDialog.EqualizerGainChanged += (s, frequency) =>
            {
                int feq = ToolUtils.FrequencyIndexMap[frequency];
                App.Services.GetRequiredService<BassPlayerCommandService>().SetEqualizerGain(feq, (float)AppSettings.equalizer[frequency]);
            };
            EqualizerDialog.clearEqualizer += (s, e) =>
            {
                App.Services.GetRequiredService<BassPlayerCommandService>().UpdateSettings();
                if (AppSettings.IsEqualizerEnabled)
                {
                    App.Services.GetRequiredService<BassPlayerCommandService>().ToggleEqualizer();
                    App.Services.GetRequiredService<BassPlayerCommandService>().SetEqualizer();
                }
                else
                {
                    App.Services.GetRequiredService<BassPlayerCommandService>().ClearEqualizer();
                }
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
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

        public void UpdateAppNotifyIconControl()
        {
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
                    await MusicDatabaseService.LoadMusicList();
                });
                await Task.Delay(500);
                App.Services.GetRequiredService<IpcService>().Initializing();
                await Task.Delay(500);
                await Task.WhenAll(longOpsTask);
                await App.Services.GetRequiredService<IpcService>().InitializeMusic(App.Services.GetRequiredService<AppViewModel>().CurrentPlayingMusic);
                AppSettings.FontFamilyList = ToolUtils.GetSystemFontsInternal();
                NavigateToDefaultPage();
                UpdateAppNotifyIconControl();
                LoadingGrid.Visibility = Visibility.Collapsed;
                NavigationViewControl.Visibility = Visibility.Visible;
                App.Services.GetRequiredService<AppViewModel>().IsInitialized = true;
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

        public void UpdateBackdropActiveState(bool isActive)
        {
            themeStyleHelper.UpdateBackdropActiveState(isActive);
            backdropInputState?.Invoke(this, isActive);
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
                _playingNavigation.FadeDismiss(AppSettings.EntranceAnimationTime);
            }
            else
            {
                var tag = args.InvokedItemContainer.Tag.ToString();
                switch (tag)
                {
                    case "AddFolder":
                        _navigationService.Navigate(typeof(AddFolderPage), null, null, AppSettings.EntranceAnimationTime);
                        _playingNavigation.FadeDismiss(AppSettings.EntranceAnimationTime);
                        break;
                    case "MusicBrowse":
                        _navigationService.Navigate(typeof(MusicBrowsePage), null, null, AppSettings.EntranceAnimationTime);
                        if (AppData.IsPlayingDetail) {
                            NavigateToPlayingDetailPage();
                        }                        
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

        public void NavigateToPlayingDetailPage()
        {
            AppTitleBarVisibility(false);
            if (PlayingFrame.Visibility is Visibility.Collapsed)
            {
                _navigationService.FadeDismiss(AppSettings.EntranceAnimationTime);
                _playingNavigation.Show(typeof(PlayingDetailPage), AppSettings.EntranceAnimationTime);
            }
        }

        public void NavigatebackToMusicBrowsePage()
        {
            if (PlayingFrame.Visibility is Visibility.Visible)
            {
                AppTitleBarVisibility(true);
                NavigationViewExpanded();
                _navigationService.FadeShow(AppSettings.EntranceAnimationTime);
                _playingNavigation.Dismiss(AppSettings.EntranceAnimationTime);
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
            if (AppData.IsPlayingDetail && PlayingFrame?.Content?.GetType() == typeof(PlayingDetailPage))
            {
                NavigationViewControlGrid.Opacity = 0;
            }
        }
    }
}
