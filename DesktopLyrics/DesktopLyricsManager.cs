using Microsoft.Extensions.DependencyInjection;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词窗口生命周期服务：创建 / 关闭 / 重置边界 / 启动恢复 / 退出清理。
    /// 开关、锁定、样式、边界等状态由 <see cref="DesktopLyricsViewModel"/> 持有（INPC 绑定源），
    /// 本类仅保存窗口生命周期状态：初始锁定态取自 VM，后续变化由窗口经 VM.PropertyChanged 感知。
    /// 所有方法须在 UI 线程调用。
    /// </summary>
    public static class DesktopLyricsManager
    {
        private static DesktopLyricsWindow? _window;
        private static IDesktopLyricsHost? _host;
        private static bool _isShuttingDown;

        private static DesktopLyricsViewModel ViewModel =>
            App.Services.GetRequiredService<DesktopLyricsViewModel>();

        /// <summary>自动隐藏复用窗口；首次被抑制时延迟创建，恢复显示不抢主窗口焦点。</summary>
        public static void SetWindowVisible(bool visible)
        {
            if (_isShuttingDown) return;
            if (visible) CreateWindow();
            _window?.SetOverlayVisible(visible);
            _host?.SetVisible(visible);
        }

        /// <summary>模式变化时重建宿主窗口；歌词渲染器状态通过总线重新同步。</summary>
        public static void RecreateForMode()
        {
            if (_isShuttingDown || _window is null) return;
            CloseWindow();
            if (ViewModel.IsEnabled)
                SetWindowVisible(!(ViewModel.ShouldAutoHideOnPlayingDetail &&
                    ViewModel.IsPlayingDetailVisible && ViewModel.IsMainWindowShown));
        }

        private static void CreateWindow()
        {
            if (_window is not null) return;
            DesktopLyricsWindow? window = null;
            IDesktopLyricsHost? host = null;
            try
            {
                window = new DesktopLyricsWindow(ViewModel.Mode);
                host = CreateHost(ViewModel.Mode, window);
                _window = window;
                _host = host;
                if (host is IDesktopLyricsBoundsHost taskbarHost)
                    window.AttachTaskbarHost(taskbarHost);
                // 必须先显示再应用锁定：对未激活的窗口做 GWL_STYLE 切 Popup / 加 WS_EX_LAYERED
                // 会破坏 XAML 岛的呈现与输入管线，后续解锁时窗口无响应且内容丢失。
                window.AppWindow.Show(false);
                window.ApplyLock(ViewModel.IsLocked);
                host.Attach(WinRT.Interop.WindowNative.GetWindowHandle(window), window.DispatcherQueue);
            }
            catch (Exception ex)
            {
                Serilog.Log.Logger.Warning(ex, "创建桌面歌词宿主失败，已回退到悬浮窗口");
                _host = null;
                _window = null;
                try { host?.Dispose(); } catch { }
                try { window?.Close(); } catch { }
                if (ViewModel.Mode == DesktopLyricsMode.Wallpaper)
                    ViewModel.Mode = DesktopLyricsMode.Floating;
            }
        }

        private static IDesktopLyricsHost CreateHost(DesktopLyricsMode mode, DesktopLyricsWindow window)
        {
            if (mode == DesktopLyricsMode.Taskbar)
            {
                AppViewModel appViewModel = App.Services.GetRequiredService<AppViewModel>();
                return new TaskbarDesktopLyricsHost(
                    ViewModel.BoundsState,
                    () => ViewModel.IsLocked,
                    () => appViewModel.DesktopLyricsTaskbarLyricsWidth);
            }

            if (mode == DesktopLyricsMode.Wallpaper)
            {
                return new WallpaperDesktopLyricsHost(
                    WinRT.Interop.WindowNative.GetWindowHandle(window),
                    window.SetWallpaperSuspended);
            }

            return new FloatingDesktopLyricsHost();
        }

        public static void CloseWindow()
        {
            var window = _window;
            _window = null;
            var host = _host;
            _host = null;
            try
            {
                host?.Dispose();
            }
            catch
            {
                // 宿主清理失败不能阻止窗口本身释放。
            }
            if (window is null) return;
            try
            {
                window.Close();
            }
            catch
            {
                // 退出过程中窗口可能已释放
            }
        }

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（窗口/托盘重置按钮调用）。</summary>
        public static void ResetWindowBounds() => _window?.ApplyDefaultBounds();

        /// <summary>应用启动时按设置恢复（AppInitializerService 调用）。</summary>
        public static void RestoreFromSettings() => ViewModel.RestoreFromSettings();

        /// <summary>应用退出清理（App.Current_Exit 调用）。边界为同步写，保证在 Environment.Exit 前完成。</summary>
        public static void Shutdown()
        {
            _isShuttingDown = true;
            CloseWindow();
            ViewModel.PersistBounds();
        }
    }
}
