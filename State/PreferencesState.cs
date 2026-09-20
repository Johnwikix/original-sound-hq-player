using CommunityToolkit.Mvvm.ComponentModel;
using System;
using Windows.UI;
using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using Microsoft.UI.Xaml;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;

using System.ComponentModel;

namespace WinUIMusicPlayer.State;

/// <summary>领域偏好的稳定聚合入口。平面属性仅兼容迁移期绑定，无重复字段。</summary>
public sealed class PreferencesState : ObservableObject
{
    public AppearancePreferencesState Appearance { get; } = new();
    public LyricsPreferencesState Lyrics { get; } = new();
    public AudioPreferencesState Audio { get; } = new();
    public GeneralPreferencesState General { get; } = new();
    public PreferencesState()
    {
        Appearance.PropertyChanged += ForwardChange;
        Lyrics.PropertyChanged += ForwardChange;
        Audio.PropertyChanged += ForwardChange;
        General.PropertyChanged += ForwardChange;
    }
    public bool IsHoverScrollEnabled { get => Appearance.IsHoverScrollEnabled; set => Appearance.IsHoverScrollEnabled = value; }
    public string DefaultEntryComboBoxTag { get => General.DefaultEntryComboBoxTag; set => General.DefaultEntryComboBoxTag = value; }
    public string ThemeType { get => Appearance.ThemeType; set => Appearance.ThemeType = value; }
    public FontInfo DesktopLyricsFontFamily { get => Lyrics.DesktopLyricsFontFamily; set => Lyrics.DesktopLyricsFontFamily = value; }
    private void ForwardChange(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(e);

    public bool UseImageDominantTheme { get => Appearance.UseImageDominantTheme; set => Appearance.UseImageDominantTheme = value; }
    public bool IsFluidBackgroundEnabled { get => Appearance.IsFluidBackgroundEnabled; set => Appearance.IsFluidBackgroundEnabled = value; }
    public bool IsFogEffectEnabled { get => Appearance.IsFogEffectEnabled; set => Appearance.IsFogEffectEnabled = value; }
    public bool IsSnowEffectEnabled { get => Appearance.IsSnowEffectEnabled; set => Appearance.IsSnowEffectEnabled = value; }
    public bool IsRaindropEffectEnabled { get => Appearance.IsRaindropEffectEnabled; set => Appearance.IsRaindropEffectEnabled = value; }
    public Thickness LyricsMargin { get => Lyrics.LyricsMargin; set => Lyrics.LyricsMargin = value; }
    public bool EnableLightWave { get => Appearance.EnableLightWave; set => Appearance.EnableLightWave = value; }
    public bool IsWin2dAnimatedText { get => Appearance.IsWin2dAnimatedText; set => Appearance.IsWin2dAnimatedText = value; }
    public int Latency { get => Audio.Latency; set => Audio.Latency = value; }
    public string DefaultPlayListComboBoxTag { get => General.DefaultPlayListComboBoxTag; set => General.DefaultPlayListComboBoxTag = value; }
    public EffectComboBoxItem Win2dTextEffectType { get => Appearance.Win2dTextEffectType; set => Appearance.Win2dTextEffectType = value; }
    public bool IsFolderWatchEnabled { get => General.IsFolderWatchEnabled; set => General.IsFolderWatchEnabled = value; }
    public bool IsMusicInfoVisible { get => Appearance.IsMusicInfoVisible; set => Appearance.IsMusicInfoVisible = value; }
    public bool EnableAdvancedLyricsEffect { get => Lyrics.EnableAdvancedLyricsEffect; set => Lyrics.EnableAdvancedLyricsEffect = value; }
    public AnimatedWin2dControls.Impressionist.PaletteAlgorithm PaletteAlgorithm { get => Appearance.PaletteAlgorithm; set => Appearance.PaletteAlgorithm = value; }
    public AnimatedWin2dControls.BackgroundShaderMode BackgroundShader { get => Appearance.BackgroundShader; set => Appearance.BackgroundShader = value; }
    public int CoverSize { get => Appearance.CoverSize; set => Appearance.CoverSize = value; }
    public int DsdGain { get => Audio.DsdGain; set => Audio.DsdGain = value; }
    public bool IsAutoLyricsEnabled { get => Lyrics.IsAutoLyricsEnabled; set => Lyrics.IsAutoLyricsEnabled = value; }
    public string ArtistSplitSymbols { get => General.ArtistSplitSymbols; set => General.ArtistSplitSymbols = value; }
    public bool IsAutoCoverEnabled { get => General.IsAutoCoverEnabled; set => General.IsAutoCoverEnabled = value; }
    public bool IsRunningBackend { get => General.IsRunningBackend; set => General.IsRunningBackend = value; }
    public bool IsCustomAppSize { get => Appearance.IsCustomAppSize; set => Appearance.IsCustomAppSize = value; }
    public int AppWidth { get => Appearance.AppWidth; set => Appearance.AppWidth = value; }
    public int AppHeight { get => Appearance.AppHeight; set => Appearance.AppHeight = value; }
    public string BackdropType { get => Appearance.BackdropType; set => Appearance.BackdropType = value; }
    public bool IsDarkMode { get => Appearance.IsDarkMode; set => Appearance.IsDarkMode = value; }
    public FontInfo FontFamily { get => Appearance.FontFamily; set => Appearance.FontFamily = value; }
    public Color CustomColor { get => Appearance.CustomColor; set => Appearance.CustomColor = value; }
    public bool IsCustomLyricsColorEnabled { get => Lyrics.IsCustomLyricsColorEnabled; set => Lyrics.IsCustomLyricsColorEnabled = value; }
    public Color LyricsCustomColor { get => Lyrics.LyricsCustomColor; set => Lyrics.LyricsCustomColor = value; }
    public double DesktopLyricsFontSize { get => Lyrics.DesktopLyricsFontSize; set => Lyrics.DesktopLyricsFontSize = value; }
    public Color DesktopLyricsColor { get => Lyrics.DesktopLyricsColor; set => Lyrics.DesktopLyricsColor = value; }
    public bool IsDesktopLyricsCustomColorEnabled { get => Lyrics.IsDesktopLyricsCustomColorEnabled; set => Lyrics.IsDesktopLyricsCustomColorEnabled = value; }
    public bool IsDesktopLyricsTranslationEnabled { get => Lyrics.IsDesktopLyricsTranslationEnabled; set => Lyrics.IsDesktopLyricsTranslationEnabled = value; }
    public bool IsDesktopLyricsGlowEnabled { get => Lyrics.IsDesktopLyricsGlowEnabled; set => Lyrics.IsDesktopLyricsGlowEnabled = value; }
    public bool IsDesktopLyricsCharFloatEnabled { get => Lyrics.IsDesktopLyricsCharFloatEnabled; set => Lyrics.IsDesktopLyricsCharFloatEnabled = value; }
    public bool IsDesktopLyricsCharScaleEnabled { get => Lyrics.IsDesktopLyricsCharScaleEnabled; set => Lyrics.IsDesktopLyricsCharScaleEnabled = value; }
    public double DesktopLyricsLongSyllableThreshold { get => Lyrics.DesktopLyricsLongSyllableThreshold; set => Lyrics.DesktopLyricsLongSyllableThreshold = value; }
    public double DesktopLyricsGlowAmount { get => Lyrics.DesktopLyricsGlowAmount; set => Lyrics.DesktopLyricsGlowAmount = value; }
    public double DesktopLyricsCharFloatAmount { get => Lyrics.DesktopLyricsCharFloatAmount; set => Lyrics.DesktopLyricsCharFloatAmount = value; }
    public double DesktopLyricsCharScaleAmount { get => Lyrics.DesktopLyricsCharScaleAmount; set => Lyrics.DesktopLyricsCharScaleAmount = value; }
    public int DesktopLyricsFontWeight { get => Lyrics.DesktopLyricsFontWeight; set => Lyrics.DesktopLyricsFontWeight = value; }
    public double DesktopLyricsShadowAmount { get => Lyrics.DesktopLyricsShadowAmount; set => Lyrics.DesktopLyricsShadowAmount = value; }
    public int LyricsFontWeight { get => Lyrics.LyricsFontWeight; set => Lyrics.LyricsFontWeight = value; }
    public float CustomOpacity { get => Appearance.CustomOpacity; set => Appearance.CustomOpacity = value; }
    public bool IsUpdateBackDrop { get => Appearance.IsUpdateBackDrop; set => Appearance.IsUpdateBackDrop = value; }
    public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsAlignment { get => Lyrics.LyricsAlignment; set => Lyrics.LyricsAlignment = value; }
    public TextAlignment PlayingDetailAlignment { get => Appearance.PlayingDetailAlignment; set => Appearance.PlayingDetailAlignment = value; }
    public bool UsePlayingDetailAlignmentInPortrait { get => Appearance.UsePlayingDetailAlignmentInPortrait; set => Appearance.UsePlayingDetailAlignmentInPortrait = value; }
    public bool IsPortraitLayout { get => Appearance.IsPortraitLayout; set => Appearance.IsPortraitLayout = value; }
    public bool IsGlobalFontSizeEnabled { get => Lyrics.IsGlobalFontSizeEnabled; set => Lyrics.IsGlobalFontSizeEnabled = value; }
    public double GlobalFontSize { get => Lyrics.GlobalFontSize; set => Lyrics.GlobalFontSize = value; }
    public double LyricsFontSize { get => Lyrics.LyricsFontSize; set => Lyrics.LyricsFontSize = value; }
    public string MusicCoverCache { get => General.MusicCoverCache; set => General.MusicCoverCache = value; }
    public bool IsFadeEnabled { get => Audio.IsFadeEnabled; set => Audio.IsFadeEnabled = value; }
    public int DsdPcmFreq { get => Audio.DsdPcmFreq; set => Audio.DsdPcmFreq = value; }
    public float LyricsBlurAmount { get => Lyrics.LyricsBlurAmount; set => Lyrics.LyricsBlurAmount = value; }
    public double CharFloatAmount { get => Lyrics.CharFloatAmount; set => Lyrics.CharFloatAmount = value; }
    public double CharScaleAmount { get => Lyrics.CharScaleAmount; set => Lyrics.CharScaleAmount = value; }
    public double GlowAmount { get => Lyrics.GlowAmount; set => Lyrics.GlowAmount = value; }
    public double LongSyllableThreshold { get => Lyrics.LongSyllableThreshold; set => Lyrics.LongSyllableThreshold = value; }
    public double PlayingLineTopOffsetPercent { get => Lyrics.PlayingLineTopOffsetPercent; set => Lyrics.PlayingLineTopOffsetPercent = value; }
    public double TranslatedOpacityPercent { get => Lyrics.TranslatedOpacityPercent; set => Lyrics.TranslatedOpacityPercent = value; }
    public double UnplayedOpacityPercent { get => Lyrics.UnplayedOpacityPercent; set => Lyrics.UnplayedOpacityPercent = value; }
    public double TargetFrameRate { get => Lyrics.TargetFrameRate; set => Lyrics.TargetFrameRate = value; }
    public EasingType ScrollEasingType { get => Lyrics.ScrollEasingType; set => Lyrics.ScrollEasingType = value; }
    public EaseMode ScrollEasingMode { get => Lyrics.ScrollEasingMode; set => Lyrics.ScrollEasingMode = value; }
    public bool EnableGlobalHotKey { get => General.EnableGlobalHotKey; set => General.EnableGlobalHotKey = value; }
    public bool IsTrimOnHideEnabled { get => General.IsTrimOnHideEnabled; set => General.IsTrimOnHideEnabled = value; }
    public bool IsTrimAfterPlaybackEnabled { get => General.IsTrimAfterPlaybackEnabled; set => General.IsTrimAfterPlaybackEnabled = value; }
}
