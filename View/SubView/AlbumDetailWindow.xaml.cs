using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.System;
using WinUIEx;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIMusicPlayer.View.SubView
{
    /// <summary>独立专辑属性窗口，与歌曲属性窗口共用编辑会话边界；布局由 AlbumDetailControl 提供。</summary>
    public sealed partial class AlbumDetailWindow : WinUIEx.WindowEx
    {
        private static readonly ILogger<AlbumDetailWindow> _logger = App.GetLogger<AlbumDetailWindow>();

        public AlbumDetailsViewModel? ViewModel { get; private set; }
        private ThemeStyleHelper themeStyleHelper;
        private bool _closed;

        public AlbumDetailWindow(Music music)
        {
            InitializeComponent();
            AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
            AppWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
            this.SetTitleBarBackgroundColors(Colors.Transparent);
            SetTitleBar(AlbumDetailTitleBar);
            SetWindow();
            Open(music);
            themeStyleHelper = new ThemeStyleHelper(this, this.AppWindow);
            themeStyleHelper.SetAppStyle();
            themeStyleHelper.SetAppTheme();
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged += MainWindow_themeChanged;
                App.MainWindow.styleChanged += MainWindow_styleChanged;
                App.MainWindow.customStyleChanged += MainWindow_customStyleChanged;
                App.MainWindow.backdropInputState += MainWindow_backdropInputState;
            }
            Title = ToolUtils.GetString("AlbumDetailTitle");
            this.Closed += AlbumDetailWindow_Closed;
        }

        private void Open(Music music)
        {
            try
            {
                ViewModel = App.Services.GetRequiredService<IMusicDetailsEditorFactory>().CreateAlbum(music);
                ViewModel.CloseRequested += CloseRequested;
                ViewModel.Start();
                DetailView.ViewModel = ViewModel;
                Bindings.Update();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "打开专辑详情失败");
                DispatcherQueue.TryEnqueue(() => { if (!_closed) Close(); });
            }
        }

        private void SetWindow()
        {
            // 默认 900×675（与歌曲属性窗口一致）；控件宽 <810 或高 <540 进入紧凑布局。
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowSizeHelper.ResizeWindowAndCenterInMainWindow(hwnd, 675, 900, App.MainWindow.AppWindow, this.AppWindow);
            this.AppWindow.SetIcon("Assets/icon.ico");
        }

        private void Root_KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (args.Key != VirtualKey.Escape) return;
            Close();
            args.Handled = true;
        }

        private void CloseRequested(object? sender, EventArgs args)
        {
            DispatcherQueue.TryEnqueue(() => { if (!_closed) Close(); });
        }

        private void MainWindow_backdropInputState(object? sender, bool e)
        {
            themeStyleHelper?.UpdateBackdropActiveState(e);
        }
        private void MainWindow_customStyleChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.ChangeCustomAcrylicStyle();
        }
        private void MainWindow_styleChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppStyle();
        }
        private void MainWindow_themeChanged(object? sender, EventArgs e)
        {
            themeStyleHelper.SetAppTheme();
        }

        private void AlbumDetailWindow_Closed(object sender, WindowEventArgs args)
        {
            _closed = true;
            if (ViewModel is { } viewModel)
            {
                ViewModel = null;
                viewModel.CloseRequested -= CloseRequested;
                viewModel.BeginClose();
                DetailView.ViewModel = null;
                Bindings.StopTracking();
                // StopAsync 自行观察异常；EditorSessions 保留退出屏障直到真实工作完成。
                _ = viewModel.StopAsync();
            }
            if (App.MainWindow is not null)
            {
                App.MainWindow.themeChanged -= MainWindow_themeChanged;
                App.MainWindow.styleChanged -= MainWindow_styleChanged;
                App.MainWindow.customStyleChanged -= MainWindow_customStyleChanged;
                App.MainWindow.backdropInputState -= MainWindow_backdropInputState;
            }
            this.Closed -= AlbumDetailWindow_Closed;
        }
    }
}
