using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using System;
using Windows.UI;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

namespace WinUIMusicPlayer.DesktopLyrics
{
    /// <summary>
    /// 桌面歌词状态源（可绑定）：开关 / 锁定 / 样式 / 窗口边界。
    /// 托盘、播放条、悬浮窗统一读写本 VM 的 INPC 属性；开关/锁定 setter 驱动
    /// DesktopLyricsManager（仅窗口生命周期 + 落盘）；悬浮窗订阅 PropertyChanged
    /// 应用锁定（GWL_STYLE）与样式推送。样式快照由 RestoreFromSettings / 设置页提交整体刷新。
    /// 所有成员须在 UI 线程访问。
    /// </summary>
    public class DesktopLyricsViewModel : ObservableObject
    {
        private bool _isEnabled;
        private bool _isLocked = true;
        private bool _isKaraokeEnabled;
        private DesktopLyricsStyle _style;
        private SaveDesktopLyricsState _boundsState = new();
        private bool _boundsLoaded;

        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (SetProperty(ref _isEnabled, value))
                {
                    AppSettings.IsDesktopLyricsEnabled = value;
                    EnsureBoundsLoaded();
                    if (value) DesktopLyricsManager.CreateWindow();
                    else DesktopLyricsManager.CloseWindow();
                    PersistSettings();
                }
            }
        }

        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (SetProperty(ref _isLocked, value))
                {
                    AppSettings.IsDesktopLyricsLocked = value;
                    PersistSettings();
                }
            }
        }

        /// <summary>逐字效果开关：true = CanvasLyricsRenderer（Win2D 逐字扫光），false = TextBlockLyricsRenderer。
        /// 窗口监听本属性热切换渲染器；默认关（与主界面 EnableAdvancedLyricsEffect 先例一致）。</summary>
        public bool IsKaraokeEnabled
        {
            get => _isKaraokeEnabled;
            set
            {
                if (SetProperty(ref _isKaraokeEnabled, value))
                {
                    AppSettings.IsDesktopLyricsKaraokeEnabled = value;
                    PersistSettings();
                }
            }
        }

        /// <summary>样式快照（悬浮窗监听变化推送渲染器；RestoreFromSettings / 设置页提交时整体更新）。</summary>
        public DesktopLyricsStyle Style { get => _style; set => SetProperty(ref _style, value); }

        /// <summary>窗口边界状态（可变模型：窗口拖动/缩放时就地更新字段，落盘经 <see cref="PersistBounds"/>）。</summary>
        public SaveDesktopLyricsState BoundsState => _boundsState;

        /// <summary>启动恢复（AppInitializerService 在初始化完成后调用）：
        /// 边界/样式/开关/锁定取自持久层与 AppSettings，启用则建窗。直接置字段避免 setter 副作用重复落盘。</summary>
        public void RestoreFromSettings()
        {
            EnsureBoundsLoaded();
            Style = BuildStyleFromSettings();
            _isEnabled = AppSettings.IsDesktopLyricsEnabled;
            OnPropertyChanged(nameof(IsEnabled));
            _isLocked = AppSettings.IsDesktopLyricsLocked;
            OnPropertyChanged(nameof(IsLocked));
            _isKaraokeEnabled = AppSettings.IsDesktopLyricsKaraokeEnabled;
            OnPropertyChanged(nameof(IsKaraokeEnabled));
            if (_isEnabled) DesktopLyricsManager.CreateWindow();
        }

        /// <summary>设置页样式变更提交后调用（防抖定时器合并多次变更）。</summary>
        public void RefreshStyleFromSettings() => Style = BuildStyleFromSettings();

        /// <summary>同步落盘窗口边界。仅在窗口关闭 / 恢复默认边界 / 退出时调用；
        /// 拖动过程中的位置变化只在内存更新（窗口 OnAppWindowChanged），不落盘。</summary>
        public void PersistBounds()
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

        private void PersistSettings()
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

        private void EnsureBoundsLoaded()
        {
            if (_boundsLoaded) return;
            _boundsLoaded = true;
            try
            {
                _boundsState = App.Services.GetRequiredService<MusicDatabaseService>().LoadDesktopLyricsState();
            }
            catch
            {
                _boundsState = new SaveDesktopLyricsState();
            }
        }

        private static DesktopLyricsStyle BuildStyleFromSettings() => new(
            AppSettings.DesktopLyricsFontSize,
            AppSettings.DesktopLyricsFontFamily,
            Color.FromArgb(0xFF,
                (byte)((AppSettings.DesktopLyricsColorRgb >> 16) & 0xFF),
                (byte)((AppSettings.DesktopLyricsColorRgb >> 8) & 0xFF),
                (byte)(AppSettings.DesktopLyricsColorRgb & 0xFF)),
            AppSettings.IsDesktopLyricsOutlineEnabled,
            AppSettings.DesktopLyricsFontWeight,
            AppSettings.DesktopLyricsOutlineWidth,
            AppSettings.IsDesktopLyricsTranslationEnabled,
            AppSettings.IsDesktopLyricsGlowEnabled,
            AppSettings.IsDesktopLyricsCharFloatEnabled,
            AppSettings.IsDesktopLyricsCharScaleEnabled,
            AppSettings.DesktopLyricsLongSyllableThreshold,
            AppSettings.DesktopLyricsGlowAmount,
            AppSettings.DesktopLyricsCharFloatAmount,
            AppSettings.DesktopLyricsCharScaleAmount);
    }
}
