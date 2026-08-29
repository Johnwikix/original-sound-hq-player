using Microsoft.Extensions.DependencyInjection;
using System;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词生命周期管理：托盘开关/锁定、启动恢复、退出清理。
    /// 状态镜像到 AppSettings 并经 MusicDatabaseService.SaveSettingAsync 落盘。
    /// 所有方法须在 UI 线程调用。
    /// </summary>
    public static class DesktopLyricsManager
    {
        public static bool IsEnabled { get; private set; }
        public static bool IsLocked { get; private set; } = true;

        /// <summary>开关或锁定状态变化（供托盘菜单勾选态同步）。</summary>
        public static event Action? StateChanged;

        private static DesktopLyricsWindow? _window;

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

        /// <summary>应用启动时按设置恢复（AppInitializerService 调用）。</summary>
        public static void RestoreFromSettings()
        {
            IsEnabled = AppSettings.IsDesktopLyricsEnabled;
            IsLocked = AppSettings.IsDesktopLyricsLocked;
            if (IsEnabled) CreateWindow();
            StateChanged?.Invoke();
        }

        /// <summary>应用退出清理（App.Current_Exit 调用）。</summary>
        public static void Shutdown()
        {
            if (_window is null) return;
            CloseWindow();
            Persist();
        }

        private static void CreateWindow()
        {
            if (_window is not null) return;
            _window = new DesktopLyricsWindow();
            _window.ApplyLock(IsLocked);
            // 与 BetterLyrics 一致：创建后仅 Activate 一次以保证 XAML 内容渲染
            _window.Activate();
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
