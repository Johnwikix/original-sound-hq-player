using System.Text.Json;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.State;

namespace WinUIMusicPlayer.Services;

/// <summary>UI 到持久化的快照适配边界；读取共享状态，数据库写入不反向读 VM。</summary>
public sealed class SettingsSnapshotFactory(AppState state)
{
    public SaveSettings CaptureGeneral(SaveSettings baseline, bool audioMigrated, bool correctionsMigrated)
    {
        // 冷路径：保留未迁移字段，并隔离快捷键列表等可变对象与后台序列化。
        var newSettings = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(baseline, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
        if (audioMigrated) newSettings.ClearLegacyAudioPreferences();
        if (correctionsMigrated) newSettings.DeviceCorrections = null;
        newSettings.DefaultEntry = state.Preferences.DefaultEntryComboBoxTag;
        newSettings.DefaultPlayList = state.Preferences.DefaultPlayListComboBoxTag;
        newSettings.AppStyle = state.Preferences.BackdropType;
        newSettings.AppTheme = state.Preferences.ThemeType;
        newSettings.IsRunningBackend = state.Preferences.IsRunningBackend;
        newSettings.IsAutoLyricsEnabled = state.Preferences.IsAutoLyricsEnabled;
        newSettings.IsAutoCoverEnabled = state.Preferences.IsAutoCoverEnabled;
        newSettings.CoverSize = state.Preferences.CoverSize;
        newSettings.Win2dTextEffectType = state.Preferences.Win2dTextEffectType.Value;
        newSettings.IsFluidBackgroundEnabled = state.Preferences.IsFluidBackgroundEnabled;
        newSettings.BackgroundShader = (int)state.Preferences.BackgroundShader;
        newSettings.IsFogEffectEnabled = state.Preferences.IsFogEffectEnabled;
        newSettings.IsSnowEffectEnabled = state.Preferences.IsSnowEffectEnabled;
        newSettings.IsRaindropEffectEnabled = state.Preferences.IsRaindropEffectEnabled;
        newSettings.IsFolderWatchEnabled = state.Preferences.IsFolderWatchEnabled;
        newSettings.IsCustomAppSize = state.Preferences.IsCustomAppSize;
        newSettings.AppHeight = state.Preferences.AppHeight;
        newSettings.AppWidth = state.Preferences.AppWidth;
        newSettings.GlobalFont = state.Preferences.FontFamily.FontFamily.Source;
        newSettings.CustomAcrylicOpacity = state.Preferences.CustomOpacity;
        newSettings.CustomColorArgb = (uint)((state.Preferences.CustomColor.A << 24) | (state.Preferences.CustomColor.R << 16) | (state.Preferences.CustomColor.G << 8) | state.Preferences.CustomColor.B);
        newSettings.IsCustomLyricsColorEnabled = state.Preferences.IsCustomLyricsColorEnabled;
        newSettings.LyricsCustomColorRgb = (uint)((state.Preferences.LyricsCustomColor.R << 16) | (state.Preferences.LyricsCustomColor.G << 8) | state.Preferences.LyricsCustomColor.B);
        newSettings.IsUpdateBackDrop = state.Preferences.IsUpdateBackDrop;
        newSettings.LyricsAlignment = state.Preferences.LyricsAlignment;
        newSettings.LyricsMargin = (int)state.Preferences.LyricsMargin.Left;
        newSettings.GlobalFontSize = state.Preferences.GlobalFontSize;
        newSettings.IsGlobalFontSizeEnabled = state.Preferences.IsGlobalFontSizeEnabled;
        newSettings.MusicCoverCache = state.Preferences.MusicCoverCache;
        newSettings.LyricsBlurAmount = state.Preferences.LyricsBlurAmount;
        newSettings.UseImageDominantTheme = state.Preferences.UseImageDominantTheme;
        newSettings.EnableLightWave = state.Preferences.EnableLightWave;
        newSettings.PaletteAlgorithm = (int)state.Preferences.PaletteAlgorithm;
        newSettings.IsWin2dAnimatedText = state.Preferences.IsWin2dAnimatedText;
        newSettings.IsHoverScrollEnabled = state.Preferences.IsHoverScrollEnabled;
        newSettings.CharFloatAmount = state.Preferences.CharFloatAmount;
        newSettings.CharScaleAmount = state.Preferences.CharScaleAmount;
        newSettings.GlowAmount = state.Preferences.GlowAmount;
        newSettings.LongSyllableThreshold = state.Preferences.LongSyllableThreshold;
        newSettings.PlayingLineTopOffsetPercent = state.Preferences.PlayingLineTopOffsetPercent;
        newSettings.TranslatedOpacityPercent = state.Preferences.TranslatedOpacityPercent;
        newSettings.UnplayedOpacityPercent = state.Preferences.UnplayedOpacityPercent;
        newSettings.TargetFrameRate = state.Preferences.TargetFrameRate;
        newSettings.EnableAdvancedLyricsEffect = state.Preferences.EnableAdvancedLyricsEffect;
        newSettings.ScrollEasingType = state.Preferences.ScrollEasingType;
        newSettings.ScrollEasingMode = state.Preferences.ScrollEasingMode;
        newSettings.PlayOrPauseShortcut = new(state.HotKeys.PlayOrPauseShortcut);
        newSettings.NextSongShortcut = new(state.HotKeys.NextSongShortcut);
        newSettings.PreviousSongShortcut = new(state.HotKeys.PreviousSongShortcut);
        newSettings.VolumeUpShortcut = new(state.HotKeys.VolumeUpShortcut);
        newSettings.VolumeDownShortcut = new(state.HotKeys.VolumeDownShortcut);
        newSettings.TogglePlayingDetailShortcut = new(state.HotKeys.TogglePlayingDetailShortcut);
        newSettings.BackShortcut = new(state.HotKeys.BackShortcut);
        newSettings.ShowWindowShortcut = new(state.HotKeys.ShowWindowShortcut);
        newSettings.ToggleFullScreenShortcut = new(state.HotKeys.ToggleFullScreenShortcut);
        newSettings.ToggleDesktopLyricsShortcut = new(state.HotKeys.ToggleDesktopLyricsShortcut);
        newSettings.ToggleDesktopLyricsLockShortcut = new(state.HotKeys.ToggleDesktopLyricsLockShortcut);
        newSettings.ToggleDesktopLyricsKaraokeShortcut = new(state.HotKeys.ToggleDesktopLyricsKaraokeShortcut);
        newSettings.ResetDesktopLyricsShortcut = new(state.HotKeys.ResetDesktopLyricsShortcut);
        newSettings.EnableGlobalHotKey = state.Preferences.EnableGlobalHotKey;
        newSettings.IsTrimOnHideEnabled = state.Preferences.IsTrimOnHideEnabled;
        newSettings.IsTrimAfterPlaybackEnabled = state.Preferences.IsTrimAfterPlaybackEnabled;
        newSettings.ArtistSplitSymbols = state.Preferences.ArtistSplitSymbols;
        newSettings.PlayingDetailAlignment = state.Preferences.PlayingDetailAlignment;
        newSettings.UsePlayingDetailAlignmentInPortrait = state.Preferences.UsePlayingDetailAlignmentInPortrait;
        newSettings.AutoHideDesktopLyricsOnPlayingDetail = AppSettings.AutoHideDesktopLyricsOnPlayingDetail;
        newSettings.IsDesktopLyricsEnabled = AppSettings.IsDesktopLyricsEnabled;
        newSettings.IsDesktopLyricsLocked = AppSettings.IsDesktopLyricsLocked;
        newSettings.IsDesktopLyricsKaraokeEnabled = AppSettings.IsDesktopLyricsKaraokeEnabled;
        newSettings.DesktopLyricsFontSize = AppSettings.DesktopLyricsFontSize;
        newSettings.DesktopLyricsFontFamily = AppSettings.DesktopLyricsFontFamily;
        newSettings.DesktopLyricsColorRgb = AppSettings.DesktopLyricsColorRgb;
        newSettings.IsDesktopLyricsCustomColorEnabled = AppSettings.IsDesktopLyricsCustomColorEnabled;
        newSettings.IsDesktopLyricsTranslationEnabled = AppSettings.IsDesktopLyricsTranslationEnabled;
        newSettings.IsDesktopLyricsGlowEnabled = AppSettings.IsDesktopLyricsGlowEnabled;
        newSettings.IsDesktopLyricsCharFloatEnabled = AppSettings.IsDesktopLyricsCharFloatEnabled;
        newSettings.IsDesktopLyricsCharScaleEnabled = AppSettings.IsDesktopLyricsCharScaleEnabled;
        newSettings.DesktopLyricsLongSyllableThreshold = AppSettings.DesktopLyricsLongSyllableThreshold;
        newSettings.DesktopLyricsGlowAmount = AppSettings.DesktopLyricsGlowAmount;
        newSettings.DesktopLyricsCharFloatAmount = AppSettings.DesktopLyricsCharFloatAmount;
        newSettings.DesktopLyricsCharScaleAmount = AppSettings.DesktopLyricsCharScaleAmount;
        newSettings.DesktopLyricsShadowAmount = AppSettings.DesktopLyricsShadowAmount;
        newSettings.DesktopLyricsFontWeight = AppSettings.DesktopLyricsFontWeight;
        newSettings.LyricsFontWeight = AppSettings.LyricsFontWeight;
        newSettings.IsMusicInfoVisible = state.Preferences.IsMusicInfoVisible;
        return newSettings;
    }

    // 在 UI 线程读取偏好一次；后台序列化期间不再访问 ViewModel。
    public AudioPreferences CaptureAudio() => new()
    {
        Dsp = AppSettings.Dsp,
        OutputMode = AppSettings.OutputMode,
        Latency = state.Preferences.Latency,
        BassOutputDeviceId = AppSettings.BassOutputDeviceId,
        WasapiEndpointId = AppSettings.WasapiEndpointId,
        BassASIODeviceId = AppSettings.BassASIODeviceId,
        DeviceFriendlyName = AppSettings.DeviceName,
        IsFadeEnabled = state.Preferences.IsFadeEnabled,
        IsDopEnabled = state.Preferences.Audio.IsDopEnabled,
        ExperimentalSurround51 = state.Preferences.Audio.ExperimentalSurround51,
        ExperimentalAtmosPassthrough = state.Preferences.Audio.ExperimentalAtmosPassthrough,
        AtmosEndpointId = state.Preferences.Audio.AtmosEndpointId,
        DsdGain = state.Preferences.DsdGain,
        DsdPcmFreq = state.Preferences.DsdPcmFreq
    };

}
