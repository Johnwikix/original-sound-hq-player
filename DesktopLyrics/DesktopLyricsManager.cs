using Microsoft.Extensions.DependencyInjection;
using System;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词窗口生命周期服务：创建 / 关闭 / 重置边界 / 启动恢复 / 退出清理。
    /// 开关、锁定、样式、边界等状态由 <see cref="DesktopLyricsViewModel"/> 持有（INPC 绑定源），
    /// 本类不保存任何状态：窗口创建时从 VM 读取初始锁定态，后续变化由窗口经 VM.PropertyChanged 感知。
    /// 所有方法须在 UI 线程调用。
    /// </summary>
    public static class DesktopLyricsManager
    {
        private static DesktopLyricsWindow? _window;

        private static DesktopLyricsViewModel ViewModel =>
            App.Services.GetRequiredService<DesktopLyricsViewModel>();

        public static void CreateWindow()
        {
            if (_window is not null) return;
            _window = new DesktopLyricsWindow();
            // 必须先显示再应用锁定：对未激活的窗口做 GWL_STYLE 切 Popup / 加 WS_EX_LAYERED
            // 会破坏 XAML 岛的呈现与输入管线，后续解锁时窗口无响应且内容丢失。
            _window.Activate();
            _window.ApplyLock(ViewModel.IsLocked);
        }

        public static void CloseWindow()
        {
            var window = _window;
            _window = null;
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
            CloseWindow();
            ViewModel.PersistBounds();
        }
    }
}
