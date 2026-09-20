using System;
using Microsoft.UI.Dispatching;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;
using System.ComponentModel;
using System.Threading.Tasks;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>偏好编辑到持久化的应用边界。启动恢复只改内存，Ready 后才接受自动保存。</summary>
public sealed class SettingsCoordinator(AppState state, MusicDatabaseService database, AppViewModel view, DesktopLyricsViewModel desktopLyrics) : IDisposable
{
    private bool _started;
    private DispatcherQueueTimer? _desktopStyleTimer;

    internal void ScheduleDesktopLyricsStyleCommit()
    {
        if (!_started || !state.Lifecycle.IsReady) return;
        if (_desktopStyleTimer is null)
        {
            _desktopStyleTimer = App.MainWindow.DispatcherQueue.CreateTimer();
            _desktopStyleTimer.Interval = TimeSpan.FromMilliseconds(300);
            _desktopStyleTimer.IsRepeating = false;
            _desktopStyleTimer.Tick += CommitDesktopStyle;
        }
        _desktopStyleTimer.Stop();
        _desktopStyleTimer.Start();
    }

    private void CommitDesktopStyle(DispatcherQueueTimer sender, object args)
    {
        if (!_started || !state.Lifecycle.IsReady) return;
        _ = database.SaveSettingAsync();
        desktopLyrics.RefreshStyleFromSettings();
    }

    public void Start()
    {
        if (_started) return;
        _started = true;
        state.Preferences.PropertyChanged += OnPreferencesChanged;
    }

    private void OnPreferencesChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(state.Preferences.PaletteAlgorithm):
                view.NotifyPreferenceComputed(nameof(view.PaletteAlgorithmIndex));
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.BackgroundShader):
                view.NotifyPreferenceComputed(nameof(view.BackgroundShaderIndex));
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.CoverSize):
                CoverLoadQueue.CoverSize = state.Preferences.CoverSize;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.DsdGain):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    AppSettings.OnOutputSettingsUpdated();
                }
                break;
            case nameof(state.Preferences.IsAutoLyricsEnabled):
                AppSettings.IsAutoLyricsEnabled = state.Preferences.IsAutoLyricsEnabled;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.ArtistSplitSymbols):
                AppSettings.ArtistSplitSymbols = state.Preferences.ArtistSplitSymbols ?? string.Empty;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.RefreshAllViews();
                }
                break;
            case nameof(state.Preferences.IsAutoCoverEnabled):
                AppSettings.IsAutoCoverEnabled = state.Preferences.IsAutoCoverEnabled;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsRunningBackend):
                AppSettings.IsRunningBackend = state.Preferences.IsRunningBackend;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsCustomAppSize):
                AppSettings.IsCustomAppSize = state.Preferences.IsCustomAppSize;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.AppWidth):
                AppSettings.AppWidth = state.Preferences.AppWidth;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.AppHeight):
                AppSettings.AppHeight = state.Preferences.AppHeight;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.BackdropType):
                AppSettings.AppStyle = state.Preferences.BackdropType;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsDarkMode):
                if (state.Lifecycle.IsReady)
                view.SendLyricsSettings();
                break;
            case nameof(state.Preferences.FontFamily):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.CustomColor):
                AppSettings.CustomColorArgb = (uint)((state.Preferences.CustomColor.A << 24) | (state.Preferences.CustomColor.R << 16) | (state.Preferences.CustomColor.G << 8) | state.Preferences.CustomColor.B);
                if (state.Lifecycle.IsReady)
                {
                    App.MainWindow?.SetCustomAppStyle();
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsCustomLyricsColorEnabled):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.LyricsCustomColor):
                AppSettings.LyricsCustomColorRgb = (uint)((state.Preferences.LyricsCustomColor.R << 16) | (state.Preferences.LyricsCustomColor.G << 8) | state.Preferences.LyricsCustomColor.B);
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsFontSize):
                AppSettings.DesktopLyricsFontSize = state.Preferences.DesktopLyricsFontSize;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsColor):
                AppSettings.DesktopLyricsColorRgb = (uint)((state.Preferences.DesktopLyricsColor.R << 16) | (state.Preferences.DesktopLyricsColor.G << 8) | state.Preferences.DesktopLyricsColor.B);
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.IsDesktopLyricsCustomColorEnabled):
                AppSettings.IsDesktopLyricsCustomColorEnabled = state.Preferences.IsDesktopLyricsCustomColorEnabled;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.IsDesktopLyricsTranslationEnabled):
                AppSettings.IsDesktopLyricsTranslationEnabled = state.Preferences.IsDesktopLyricsTranslationEnabled;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.IsDesktopLyricsGlowEnabled):
                AppSettings.IsDesktopLyricsGlowEnabled = state.Preferences.IsDesktopLyricsGlowEnabled;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.IsDesktopLyricsCharFloatEnabled):
                AppSettings.IsDesktopLyricsCharFloatEnabled = state.Preferences.IsDesktopLyricsCharFloatEnabled;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.IsDesktopLyricsCharScaleEnabled):
                AppSettings.IsDesktopLyricsCharScaleEnabled = state.Preferences.IsDesktopLyricsCharScaleEnabled;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsLongSyllableThreshold):
                AppSettings.DesktopLyricsLongSyllableThreshold = state.Preferences.DesktopLyricsLongSyllableThreshold;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsGlowAmount):
                AppSettings.DesktopLyricsGlowAmount = state.Preferences.DesktopLyricsGlowAmount;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsCharFloatAmount):
                AppSettings.DesktopLyricsCharFloatAmount = state.Preferences.DesktopLyricsCharFloatAmount;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsCharScaleAmount):
                AppSettings.DesktopLyricsCharScaleAmount = state.Preferences.DesktopLyricsCharScaleAmount;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsFontWeight):
                AppSettings.DesktopLyricsFontWeight = state.Preferences.DesktopLyricsFontWeight;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.DesktopLyricsShadowAmount):
                AppSettings.DesktopLyricsShadowAmount = state.Preferences.DesktopLyricsShadowAmount;
                if (state.Lifecycle.IsReady)
                {
                    ScheduleDesktopLyricsStyleCommit();
                }
                break;
            case nameof(state.Preferences.LyricsFontWeight):
                AppSettings.LyricsFontWeight = state.Preferences.LyricsFontWeight;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.CustomOpacity):
                AppSettings.CustomAcrylicOpacity = state.Preferences.CustomOpacity / 100;
                if (state.Lifecycle.IsReady)
                {
                    App.MainWindow?.SetCustomAppStyle();
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsUpdateBackDrop):
                AppSettings.IsUpdateBackDrop = state.Preferences.IsUpdateBackDrop;
                if (state.Lifecycle.IsReady)
                {
                    App.MainWindow?.UpdateBackdropActiveState(state.Preferences.IsUpdateBackDrop);
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.LyricsAlignment):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.PlayingDetailAlignment):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                view.NotifyPreferenceComputed(nameof(view.EffectivePlayingDetailAlignment));
                break;
            case nameof(state.Preferences.UsePlayingDetailAlignmentInPortrait):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                view.NotifyPreferenceComputed(nameof(view.EffectivePlayingDetailAlignment));
                break;
            case nameof(state.Preferences.IsPortraitLayout):
                view.NotifyPreferenceComputed(nameof(view.EffectivePlayingDetailAlignment));
                if (state.Lifecycle.IsReady)
                {
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.IsGlobalFontSizeEnabled):
                AppSettings.IsGlobalFontSizeEnabled = state.Preferences.IsGlobalFontSizeEnabled;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsFontSize();
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.GlobalFontSize):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsFontSize();
                }
                break;
            case nameof(state.Preferences.LyricsFontSize):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsFontSize();
                }
                break;
            case nameof(state.Preferences.MusicCoverCache):
                AppSettings.MusicCoverCache = state.Preferences.MusicCoverCache;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsFadeEnabled):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    AppSettings.OnOutputSettingsUpdated();
                }
                break;
            case nameof(state.Preferences.DsdPcmFreq):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    AppSettings.OnOutputSettingsUpdated();
                }
                break;
            case nameof(state.Preferences.LyricsBlurAmount):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.CharFloatAmount):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.CharScaleAmount):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.GlowAmount):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.LongSyllableThreshold):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.PlayingLineTopOffsetPercent):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.TranslatedOpacityPercent):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.UnplayedOpacityPercent):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.TargetFrameRate):
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.SendLyricsSettings();
                }
                break;
            case nameof(state.Preferences.ScrollEasingType):
                view.NotifyPreferenceComputed(nameof(view.ScrollEasingTypeIndex));
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.ScrollEasingMode):
                view.NotifyPreferenceComputed(nameof(view.ScrollEasingModeIndex));
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.ScheduleSettingsBroadcast();
                }
                break;
            case nameof(state.Preferences.EnableGlobalHotKey):
                AppSettings.EnableGlobalHotKey = state.Preferences.EnableGlobalHotKey;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                    view.InitHotKeys();
                }
                break;
            case nameof(state.Preferences.IsTrimOnHideEnabled):
                AppSettings.IsTrimOnHideEnabled = state.Preferences.IsTrimOnHideEnabled;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            case nameof(state.Preferences.IsTrimAfterPlaybackEnabled):
                AppSettings.IsTrimAfterPlaybackEnabled = state.Preferences.IsTrimAfterPlaybackEnabled;
                if (state.Lifecycle.IsReady)
                {
                    _ = database.SaveSettingAsync();
                }
                break;
            default:
                if (state.Lifecycle.IsReady) _ = database.SaveSettingAsync();
                break;
        }
    }


    public Task FlushAsync() => database.FlushSettingsAsync();

    public void Dispose()
    {
        if (!_started) return;
        _started = false;
        _desktopStyleTimer?.Stop();
        if (_desktopStyleTimer is not null) _desktopStyleTimer.Tick -= CommitDesktopStyle;
        state.Preferences.PropertyChanged -= OnPreferencesChanged;
    }
}
