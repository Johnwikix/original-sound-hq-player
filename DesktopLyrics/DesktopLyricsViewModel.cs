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
        public WinUIMusicPlayer.State.AppState State { get; }
        public DesktopLyricsViewModel(WinUIMusicPlayer.State.AppState state, MusicDatabaseService database, ShutdownCoordinator shutdown)
        {
            State = state;
            _database = database;
            shutdown.RegisterCleanup(() => State.DesktopLyrics.PropertyChanged -= OnSharedStateChanged);
            State.DesktopLyrics.PropertyChanged += OnSharedStateChanged;
        }

        private bool _restoring;
        private readonly MusicDatabaseService _database;
        private void OnSharedStateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            OnPropertyChanged(e);
            if (_restoring) return;
            switch (e.PropertyName)
            {
                case nameof(IsEnabled):
                    AppSettings.IsDesktopLyricsEnabled = IsEnabled;
                    EnsureBoundsLoaded();
                    if (IsEnabled) UpdateWindowVisibility();
                    else DesktopLyricsManager.CloseWindow();
                    PersistSettings();
                    break;
                case nameof(IsLocked):
                    AppSettings.IsDesktopLyricsLocked = IsLocked;
                    PersistSettings();
                    break;
                case nameof(AutoHideOnPlayingDetail):
                    AppSettings.AutoHideDesktopLyricsOnPlayingDetail = AutoHideOnPlayingDetail;
                    UpdateWindowVisibility();
                    PersistSettings();
                    break;
                case nameof(IsMainWindowShown):
                case nameof(IsPlayingDetailVisible):
                    UpdateWindowVisibility();
                    break;
            }
        }

        public bool IsEnabled { get => State.DesktopLyrics.IsEnabled; set => State.DesktopLyrics.IsEnabled = value; }
        public bool IsLocked { get => State.DesktopLyrics.IsLocked; set => State.DesktopLyrics.IsLocked = value; }
        public bool IsKaraokeEnabled { get => State.DesktopLyrics.IsKaraokeEnabled; set => State.DesktopLyrics.IsKaraokeEnabled = value; }
        public bool AutoHideOnPlayingDetail { get => State.DesktopLyrics.AutoHideOnPlayingDetail; set => State.DesktopLyrics.AutoHideOnPlayingDetail = value; }
        public bool IsMainWindowShown { get => State.DesktopLyrics.IsMainWindowShown; set => State.DesktopLyrics.IsMainWindowShown = value; }
        public bool IsPlayingDetailVisible { get => State.DesktopLyrics.IsPlayingDetailVisible; set => State.DesktopLyrics.IsPlayingDetailVisible = value; }

        private void UpdateWindowVisibility()
        {
            if (!IsEnabled || State.Lifecycle.Phase == AppPhase.Stopping) return;
            DesktopLyricsManager.SetWindowVisible(!(AutoHideOnPlayingDetail && IsPlayingDetailVisible && IsMainWindowShown));
        }

        private DesktopLyricsStyle _style;
        private SaveDesktopLyricsState _boundsState = new();
        private bool _boundsLoaded;

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
            _restoring = true;
            try
            {
                IsEnabled = AppSettings.IsDesktopLyricsEnabled;
                IsLocked = AppSettings.IsDesktopLyricsLocked;
                IsKaraokeEnabled = AppSettings.IsDesktopLyricsKaraokeEnabled;
                AutoHideOnPlayingDetail = AppSettings.AutoHideDesktopLyricsOnPlayingDetail;
            }
            finally { _restoring = false; }
            UpdateWindowVisibility();
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
                _database.SaveDesktopLyricsState(BoundsState);
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
                // 启动水合期间 GetSettingsAsync 回填开关会走到这里，此时全量落盘会把
                // 尚未加载完的设置写成默认值，故初始化完成前只更新内存不写盘
                if (!State.Lifecycle.IsReady) return;
                _ = _database.SaveSettingAsync();
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
                _boundsState = _database.LoadDesktopLyricsState();
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
            AppSettings.DesktopLyricsFontWeight,
            AppSettings.IsDesktopLyricsTranslationEnabled,
            AppSettings.IsDesktopLyricsGlowEnabled,
            AppSettings.IsDesktopLyricsCharFloatEnabled,
            AppSettings.IsDesktopLyricsCharScaleEnabled,
            AppSettings.DesktopLyricsLongSyllableThreshold,
            AppSettings.DesktopLyricsGlowAmount,
            AppSettings.DesktopLyricsCharFloatAmount,
            AppSettings.DesktopLyricsCharScaleAmount,
            AppSettings.IsDesktopLyricsCustomColorEnabled,
            AppSettings.DesktopLyricsShadowAmount);
    }
}
