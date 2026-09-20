using System;
using System.ComponentModel;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinUIMusicPlayer.State;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.View;
using WinUIMusicPlayer.DesktopLyrics;

namespace WinUIMusicPlayer.Services;

/// <summary>拥有全局热键的原生注册；动作触发时才解析页面/播放命令，避免启动依赖环。</summary>
public sealed class HotKeyService(AppState state, MusicDatabaseService database, ILogger<HotKeyService> logger) : IDisposable
{
    private bool _started, _disposed;
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        state.HotKeys.PropertyChanged += Changed;
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (!state.Lifecycle.IsReady || e.PropertyName is nameof(HotKeyState.HasGlobalHotKeyConflict) or nameof(HotKeyState.GlobalHotKeyConflictTitle)) return;
        _ = database.SaveSettingAsync();
        Refresh();
    }
    private void AdjustVolume(double delta)
    {
        if (state.Lifecycle.IsReady) state.Playback.Volume = Math.Clamp(state.Playback.Volume + delta, 0, 100);
    }
    private void OnGlobalHotKeyConflictsChanged(object? sender, EventArgs e)
    {
        bool any = GlobalHotKeyHook.Conflicts.Count > 0;
        if (state.HotKeys.HasGlobalHotKeyConflict != any)
        {
            state.HotKeys.HasGlobalHotKeyConflict = any;
        }
        if (any)
        {
            string format = ToolUtils.GetString("GlobalHotKeyConflictTitleFormat");
            string list = string.Join(", ", GlobalHotKeyHook.Conflicts.Select(GlobalHotKeyHook.GetDisplayName));
            state.HotKeys.GlobalHotKeyConflictTitle = string.Format(format, list);
        }
        else
        {
            state.HotKeys.GlobalHotKeyConflictTitle = string.Empty;
        }
    }

    public void Refresh()
    {
        if (_disposed || state.Lifecycle.Phase == AppPhase.Stopping) return;
        Start();
        var window = App.MainWindow;
        if (window is null) return;

        GlobalHotKeyHook.ConflictsChanged -= OnGlobalHotKeyConflictsChanged;
        GlobalHotKeyHook.ConflictsChanged += OnGlobalHotKeyConflictsChanged;
        GlobalHotKeyHook.ClearAll(window);

        if (!state.Preferences.EnableGlobalHotKey) return;

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PlayOrPauseSong, state.HotKeys.PlayOrPauseShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            App.Services.GetRequiredService<PlaybackCommands>().ToggleCommand.Execute(null);
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.NextSong, state.HotKeys.NextSongShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            App.Services.GetRequiredService<PlaybackCommands>().NextCommand.Execute(null);
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PreviousSong, state.HotKeys.PreviousSongShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            App.Services.GetRequiredService<PlaybackCommands>().PreviousCommand.Execute(null);
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.VolumeUp, state.HotKeys.VolumeUpShortcut, () =>
        {
            AdjustVolume(5);
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.VolumeDown, state.HotKeys.VolumeDownShortcut, () =>
        {
            AdjustVolume(-5);
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.TogglePlayingDetail, state.HotKeys.TogglePlayingDetailShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            if (window is not { Visible: true }) return;
            var mainPage = App.Services.GetRequiredService<MainPage>();
            if (mainPage.IsPlayingDetailVisible)
                mainPage.NavigatebackToMusicBrowsePage();
            else
                mainPage.NavigateToPlayingDetailPage();
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.Back, state.HotKeys.BackShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            if (window is not { Visible: true }) return;
            App.Services.GetRequiredService<MainPage>().HandleBackNavigation();
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ShowWindow, state.HotKeys.ShowWindowShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            window.ToggleShowHide();
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleFullScreen, state.HotKeys.ToggleFullScreenShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            if (App.MainWindow is not { Visible: true }) return;
            state.Shell.IsFullScreen = !state.Shell.IsFullScreen;
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyrics, state.HotKeys.ToggleDesktopLyricsShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
            desktopLyrics.IsEnabled = !desktopLyrics.IsEnabled;
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyricsLock, state.HotKeys.ToggleDesktopLyricsLockShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
            desktopLyrics.IsLocked = !desktopLyrics.IsLocked;
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyricsKaraoke, state.HotKeys.ToggleDesktopLyricsKaraokeShortcut, () =>
        {
            if (_disposed || !state.Lifecycle.IsReady) return;
            var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
            desktopLyrics.IsKaraokeEnabled = !desktopLyrics.IsKaraokeEnabled;
        });

        GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ResetDesktopLyrics, state.HotKeys.ResetDesktopLyricsShortcut, () => { if (!_disposed && state.Lifecycle.IsReady) DesktopLyricsManager.ResetWindowBounds(); });
    }


    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        state.HotKeys.PropertyChanged -= Changed;
        GlobalHotKeyHook.ConflictsChanged -= OnGlobalHotKeyConflictsChanged;
        try { if (App.MainWindow is { } window) GlobalHotKeyHook.ClearAll(window); }
        catch (Exception ex) { logger.LogError(ex, "注销全局快捷键失败"); }
    }
}
