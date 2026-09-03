using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using WinUIEx;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.Controls
{
    /// <summary>
    /// 托盘图标与上下文菜单。状态展示全部经 x:Bind 绑定 VM/转换器；
    /// 播放控制/播放模式/音量命令来自对应 VM，窗口激活与导航等托盘组合命令在本类。
    /// 桌面歌词开关/逐字/锁定读写 <see cref="DesktopLyricsViewModel"/>（INPC 绑定源，setter 驱动窗口生命周期）。
    /// </summary>
    public sealed partial class NotifyIconControl : Microsoft.UI.Xaml.Controls.UserControl, IDisposable
    {
        public MusicBrowseViewModel MusicBrowseViewModel { get; }
        public AppViewModel AppViewModel { get; }
        public DesktopLyricsViewModel DesktopLyrics { get; }

        public NotifyIconControl()
        {
            this.InitializeComponent();
            MusicBrowseViewModel = App.Services.GetRequiredService<MusicBrowseViewModel>();
            AppViewModel = App.Services.GetRequiredService<AppViewModel>();
            DesktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
        }

        // ==== 托盘组合命令（窗口激活/导航/桌面歌词） ====

        [RelayCommand]
        private void ToggleDesktopLyrics() => DesktopLyrics.IsEnabled = !DesktopLyrics.IsEnabled;

        [RelayCommand]
        private void ToggleDesktopLyricsKaraoke() => DesktopLyrics.IsKaraokeEnabled = !DesktopLyrics.IsKaraokeEnabled;

        [RelayCommand]
        private void ToggleDesktopLyricsLock() => DesktopLyrics.IsLocked = !DesktopLyrics.IsLocked;

        [RelayCommand]
        private void ResetDesktopLyricsBounds() => DesktopLyricsManager.ResetWindowBounds();

        [RelayCommand]
        private void OpenSettings()
        {
            ShowWindow(App.MainWindow);
            App.Services.GetRequiredService<MainPage>()?.NavigateToSettingsPage();
        }

        [RelayCommand]
        public async Task ExitApplication()
        {
            await App.Current_Exit();
        }

        [RelayCommand]
        public void ShowHideWindow()
        {
            ShowWindow(App.MainWindow);
        }

        [RelayCommand]
        public void ShowPlayingDetail()
        {
            ShowWindow(App.MainWindow);
            App.MainWindow.DispatcherQueue.TryEnqueue(() =>
            {
                App.Services.GetRequiredService<MainPage>().NavigateToPlayingDetailPage();
            });
        }

        private void ShowWindow(MainWindow window)
        {
            if (WindowHelper.IsWindowVisible(AppData.HWnd))
            {
                if (window.AppWindow.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen)
                {
                    //window.Restore();
                    window.Activate();
                    window.SetForegroundWindow();
                }
            }
            else
            {
                if (window is null)
                {
                    return;
                }
                if (!window.Visible)
                {
                    window.Show();
                    window.InitializeTaskbarHelper();
                }
            }
        }

        public void Dispose()
        {
            try
            {
                NotifyIcon?.Dispose();
                NotifyIcon = null;
            }
            catch (Exception)
            {
                NotifyIcon = null;
            }
        }
    }
}
