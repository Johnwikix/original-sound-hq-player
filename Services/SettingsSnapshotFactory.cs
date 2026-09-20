using System.Text.Json;
using BassPlayerIpc.Shared;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

/// <summary>UI 到持久化的快照适配边界；迁移中的偏好仍经兼容门面取值，数据库写入不反向读 VM。</summary>
public sealed class SettingsSnapshotFactory(AppViewModel AppViewModel)
{
    public SaveSettings CaptureGeneral(SaveSettings baseline, bool audioMigrated, bool correctionsMigrated)
    {
        // 冷路径：保留未迁移字段，并隔离快捷键列表等可变对象与后台序列化。
        var newSettings = JsonSerializer.Deserialize(JsonSerializer.SerializeToUtf8Bytes(baseline, SettingsJsonContext.Default.SaveSettings), SettingsJsonContext.Default.SaveSettings)!;
        if (audioMigrated) newSettings.ClearLegacyAudioPreferences();
        if (correctionsMigrated) newSettings.DeviceCorrections = null;
        newSettings.DefaultEntry = AppViewModel.DefaultEntryComboBoxTag;
        newSettings.DefaultPlayList = AppViewModel.DefaultPlayListComboBoxTag;
        newSettings.AppStyle = AppViewModel.BackdropType;
        newSettings.AppTheme = AppViewModel.ThemeType;
        newSettings.IsRunningBackend = AppViewModel.IsRunningBackend;
        newSettings.IsAutoLyricsEnabled = AppViewModel.IsAutoLyricsEnabled;
        newSettings.IsAutoCoverEnabled = AppViewModel.IsAutoCoverEnabled;
        newSettings.CoverSize = AppViewModel.CoverSize;
        newSettings.Win2dTextEffectType = AppViewModel.Win2dTextEffectType.Value;
        newSettings.IsFluidBackgroundEnabled = AppViewModel.IsFluidBackgroundEnabled;
        newSettings.BackgroundShader = (int)AppViewModel.BackgroundShader;
        newSettings.IsFogEffectEnabled = AppViewModel.IsFogEffectEnabled;
        newSettings.IsSnowEffectEnabled = AppViewModel.IsSnowEffectEnabled;
        newSettings.IsRaindropEffectEnabled = AppViewModel.IsRaindropEffectEnabled;
        newSettings.IsFolderWatchEnabled = AppViewModel.IsFolderWatchEnabled;
        newSettings.IsCustomAppSize = AppViewModel.IsCustomAppSize;
        newSettings.AppHeight = AppViewModel.AppHeight;
        newSettings.AppWidth = AppViewModel.AppWidth;
        newSettings.GlobalFont = AppViewModel.FontFamily.FontFamily.Source;
        newSettings.CustomAcrylicOpacity = AppViewModel.CustomOpacity;
        newSettings.CustomColorArgb = (uint)((AppViewModel.CustomColor.A << 24) | (AppViewModel.CustomColor.R << 16) | (AppViewModel.CustomColor.G << 8) | AppViewModel.CustomColor.B);
        newSettings.IsCustomLyricsColorEnabled = AppViewModel.IsCustomLyricsColorEnabled;
        newSettings.LyricsCustomColorRgb = (uint)((AppViewModel.LyricsCustomColor.R << 16) | (AppViewModel.LyricsCustomColor.G << 8) | AppViewModel.LyricsCustomColor.B);
        newSettings.IsUpdateBackDrop = AppViewModel.IsUpdateBackDrop;
        newSettings.LyricsAlignment = AppViewModel.LyricsAlignment;
        newSettings.LyricsMargin = (int)AppViewModel.LyricsMargin.Left;
        newSettings.GlobalFontSize = AppViewModel.GlobalFontSize;
        newSettings.IsGlobalFontSizeEnabled = AppViewModel.IsGlobalFontSizeEnabled;
        newSettings.MusicCoverCache = AppViewModel.MusicCoverCache;
        newSettings.LyricsBlurAmount = AppViewModel.LyricsBlurAmount;
        newSettings.UseImageDominantTheme = AppViewModel.UseImageDominantTheme;
        newSettings.EnableLightWave = AppViewModel.EnableLightWave;
        newSettings.PaletteAlgorithm = (int)AppViewModel.PaletteAlgorithm;
        newSettings.IsWin2dAnimatedText = AppViewModel.IsWin2dAnimatedText;
        newSettings.IsHoverScrollEnabled = AppViewModel.IsHoverScrollEnabled;
        newSettings.CharFloatAmount = AppViewModel.CharFloatAmount;
        newSettings.CharScaleAmount = AppViewModel.CharScaleAmount;
        newSettings.GlowAmount = AppViewModel.GlowAmount;
        newSettings.LongSyllableThreshold = AppViewModel.LongSyllableThreshold;
        newSettings.PlayingLineTopOffsetPercent = AppViewModel.PlayingLineTopOffsetPercent;
        newSettings.TranslatedOpacityPercent = AppViewModel.TranslatedOpacityPercent;
        newSettings.UnplayedOpacityPercent = AppViewModel.UnplayedOpacityPercent;
        newSettings.TargetFrameRate = AppViewModel.TargetFrameRate;
        newSettings.EnableAdvancedLyricsEffect = AppViewModel.EnableAdvancedLyricsEffect;
        newSettings.ScrollEasingType = AppViewModel.ScrollEasingType;
        newSettings.ScrollEasingMode = AppViewModel.ScrollEasingMode;
        newSettings.PlayOrPauseShortcut = new(AppViewModel.PlayOrPauseShortcut);
        newSettings.NextSongShortcut = new(AppViewModel.NextSongShortcut);
        newSettings.PreviousSongShortcut = new(AppViewModel.PreviousSongShortcut);
        newSettings.VolumeUpShortcut = new(AppViewModel.VolumeUpShortcut);
        newSettings.VolumeDownShortcut = new(AppViewModel.VolumeDownShortcut);
        newSettings.TogglePlayingDetailShortcut = new(AppViewModel.TogglePlayingDetailShortcut);
        newSettings.BackShortcut = new(AppViewModel.BackShortcut);
        newSettings.ShowWindowShortcut = new(AppViewModel.ShowWindowShortcut);
        newSettings.ToggleFullScreenShortcut = new(AppViewModel.ToggleFullScreenShortcut);
        newSettings.ToggleDesktopLyricsShortcut = new(AppViewModel.ToggleDesktopLyricsShortcut);
        newSettings.ToggleDesktopLyricsLockShortcut = new(AppViewModel.ToggleDesktopLyricsLockShortcut);
        newSettings.ToggleDesktopLyricsKaraokeShortcut = new(AppViewModel.ToggleDesktopLyricsKaraokeShortcut);
        newSettings.ResetDesktopLyricsShortcut = new(AppViewModel.ResetDesktopLyricsShortcut);
        newSettings.EnableGlobalHotKey = AppViewModel.EnableGlobalHotKey;
        newSettings.IsTrimOnHideEnabled = AppViewModel.IsTrimOnHideEnabled;
        newSettings.IsTrimAfterPlaybackEnabled = AppViewModel.IsTrimAfterPlaybackEnabled;
        newSettings.ArtistSplitSymbols = AppViewModel.ArtistSplitSymbols;
        newSettings.PlayingDetailAlignment = AppViewModel.PlayingDetailAlignment;
        newSettings.UsePlayingDetailAlignmentInPortrait = AppViewModel.UsePlayingDetailAlignmentInPortrait;
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
        newSettings.IsMusicInfoVisible = AppViewModel.IsMusicInfoVisible;
        return newSettings;
    }

    // 在 UI 线程读取偏好一次；后台序列化期间不再访问 ViewModel。
    public AudioPreferences CaptureAudio() => new()
    {
        Dsp = AppSettings.Dsp,
        OutputMode = AppSettings.OutputMode,
        Latency = AppViewModel.Latency,
        BassOutputDeviceId = AppSettings.BassOutputDeviceId,
        WasapiEndpointId = AppSettings.WasapiEndpointId,
        BassASIODeviceId = AppSettings.BassASIODeviceId,
        DeviceFriendlyName = AppSettings.DeviceName,
        IsFadeEnabled = AppViewModel.IsFadeEnabled,
        IsDopEnabled = AppViewModel.IsDopEnabled,
        ExperimentalSurround51 = AppViewModel.ExperimentalSurround51,
        ExperimentalAtmosPassthrough = AppViewModel.ExperimentalAtmosPassthrough,
        AtmosEndpointId = AppViewModel.AtmosEndpointId,
        DsdGain = AppViewModel.DsdGain,
        DsdPcmFreq = AppViewModel.DsdPcmFreq
    };

}
