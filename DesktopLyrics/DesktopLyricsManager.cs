using Microsoft.Extensions.DependencyInjection;
using System;
using Windows.UI;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词生命周期管理：托盘/播放条开关、锁定、重置窗口、样式推送、启动恢复、退出清理。
    /// 偏好项（开关/锁定/样式）镜像到 AppSettings 经 SaveSettingAsync 落盘；
    /// 窗口边界（会话状态）存独立 DesktopLyricsState.json（比照 PlayState）。
    /// 所有方法须在 UI 线程调用。
    /// </summary>
    public static class DesktopLyricsManager
    {
        public static bool IsEnabled { get; private set; }
        public static bool IsLocked { get; private set; }

        /// <summary>桌面歌词窗口边界状态（独立 DesktopLyricsState.json）。</summary>
        public static SaveDesktopLyricsState BoundsState { get; private set; } = new();

        /// <summary>开关或锁定状态变化（供托盘菜单勾选态同步）。</summary>
        public static event Action? StateChanged;

        private static DesktopLyricsWindow? _window;
        private static bool _boundsLoaded;

        public static void SetEnabled(bool enable)
        {
            if (IsEnabled == enable) return;
            IsEnabled = enable;
            AppSettings.IsDesktopLyricsEnabled = enable;
            if (enable) CreateWindow();
            else CloseWindow();
            Persist();
            StateChanged?.Invoke();
        }

        public static void SetLocked(bool locked)
        {
            if (IsLocked == locked) return;
            IsLocked = locked;
            AppSettings.IsDesktopLyricsLocked = locked;
            _window?.ApplyLock(locked);
            Persist();
            StateChanged?.Invoke();
        }

        /// <summary>恢复默认尺寸并置于主屏工作区底部居中（窗口/托盘重置按钮调用）。</summary>
        public static void ResetWindowBounds()
        {
            _window?.ApplyDefaultBounds();
            PersistBounds();
        }

        /// <summary>窗口边界变化后由防抖定时器调用：同步落盘。</summary>
        public static void PersistBounds()
        {
            EnsureBoundsLoaded();
            try
            {
                App.Services.GetRequiredService<MusicDatabaseService>().SaveDesktopLyricsState(BoundsState);
            }
            catch
            {
                // 退出阶段服务可能已释放
            }
        }

        /// <summary>设置页变更样式后推送（AppSettings 已由 VM setter 更新）。</summary>
        public static void RefreshStyle()
        {
            _window?.ApplyStyle(GetCurrentStyle());
        }

        public static DesktopLyricsStyle GetCurrentStyle() => new(
            AppSettings.DesktopLyricsFontSize,
            AppSettings.DesktopLyricsFontFamily,
            Color.FromArgb(0xFF,
                (byte)((AppSettings.DesktopLyricsColorRgb >> 16) & 0xFF),
                (byte)((AppSettings.DesktopLyricsColorRgb >> 8) & 0xFF),
                (byte)(AppSettings.DesktopLyricsColorRgb & 0xFF)),
            AppSettings.IsDesktopLyricsOutlineEnabled);

        /// <summary>应用启动时按设置恢复（AppInitializerService 调用）。</summary>
        public static void RestoreFromSettings()
        {
            IsEnabled = AppSettings.IsDesktopLyricsEnabled;
            IsLocked = AppSettings.IsDesktopLyricsLocked;
            if (IsEnabled) CreateWindow();
            StateChanged?.Invoke();
        }

        /// <summary>应用退出清理（App.Current_Exit 调用）。边界为同步写，保证在 Environment.Exit 前完成。</summary>
        public static void Shutdown()
        {
            if (_window is not null) CloseWindow();
            PersistBounds();
        }

        private static void CreateWindow()
        {
            if (_window is not null) return;
            EnsureBoundsLoaded();
            _window = new DesktopLyricsWindow();
            // 必须先显示再应用锁定：对未激活的窗口做 GWL_STYLE 切 Popup / 加 WS_EX_LAYERED
            // 会破坏 XAML 岛的呈现与输入管线，后续解锁时窗口无响应且内容丢失。
            _window.Activate();
            _window.ApplyLock(IsLocked);
        }

        private static void CloseWindow()
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

        private static void EnsureBoundsLoaded()
        {
            if (_boundsLoaded) return;
            _boundsLoaded = true;
            try
            {
                BoundsState = App.Services.GetRequiredService<MusicDatabaseService>().LoadDesktopLyricsState();
            }
            catch
            {
                BoundsState = new SaveDesktopLyricsState();
            }
        }

        private static void Persist()
        {
            try
            {
                _ = App.Services.GetRequiredService<MusicDatabaseService>().SaveSettingAsync();
            }
            catch
            {
                // 退出阶段服务可能已释放，忽略
            }
        }
    }
}
