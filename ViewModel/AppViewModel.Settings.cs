using AnimatedWin2dControls.Controls.AnimatedLyricsLineControl.Advance;
using AnimatedWin2dControls.Controls.AnimatedTextBlock.Enums;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using WinUIEx;
using WinUIMusicPlayer.Utils;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using WinUIMusicPlayer.Behaviors;
using WinUIMusicPlayer.DesktopLyrics;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        public bool IsRealDevceChange { get => State.Output.IsRealDeviceChange; set => State.Output.IsRealDeviceChange = value; }
        public bool EnableLightWave { get => State.Preferences.EnableLightWave; set => State.Preferences.EnableLightWave = value; }
        public AnimatedWin2dControls.Impressionist.PaletteAlgorithm PaletteAlgorithm { get => State.Preferences.PaletteAlgorithm; set => State.Preferences.PaletteAlgorithm = value; }
        public int PaletteAlgorithmIndex
        {
            get => (int)PaletteAlgorithm;
            set => PaletteAlgorithm = (AnimatedWin2dControls.Impressionist.PaletteAlgorithm)value;
        }
        public AnimatedWin2dControls.BackgroundShaderMode BackgroundShader { get => State.Preferences.BackgroundShader; set => State.Preferences.BackgroundShader = value; }
        public int BackgroundShaderIndex
        {
            // 历史保存值可能越界（枚举成员增删/实验构建残留），越界索引直塞
            // SelectedIndex 会抛 E_INVALIDARG 并在 x:Bind 初始化时崩掉整个对话框
            get => Math.Clamp((int)BackgroundShader, 0, MaxBackgroundShaderIndex);
            set => BackgroundShader = (AnimatedWin2dControls.BackgroundShaderMode)Math.Clamp(value, 0, MaxBackgroundShaderIndex);
        }

        private static readonly int MaxBackgroundShaderIndex =
            (int)AnimatedWin2dControls.BackgroundShaderMode.ChromaticResonance; // 枚举尾成员=最大合法索引
        public int CoverSize { get => State.Preferences.CoverSize; set => State.Preferences.CoverSize = value; }

        public bool IsHoverScrollEnabled { get => State.Preferences.IsHoverScrollEnabled; set => State.Preferences.IsHoverScrollEnabled = value; }

        public bool IsWin2dAnimatedText { get => State.Preferences.IsWin2dAnimatedText; set => State.Preferences.IsWin2dAnimatedText = value; }

        public int DsdGain { get => State.Preferences.DsdGain; set => State.Preferences.DsdGain = value; }

        public bool IsAutoLyricsEnabled { get => State.Preferences.IsAutoLyricsEnabled; set => State.Preferences.IsAutoLyricsEnabled = value; }

        public string ArtistSplitSymbols { get => State.Preferences.ArtistSplitSymbols; set => State.Preferences.ArtistSplitSymbols = value; }

        public bool IsAutoCoverEnabled { get => State.Preferences.IsAutoCoverEnabled; set => State.Preferences.IsAutoCoverEnabled = value; }

        public bool IsRunningBackend { get => State.Preferences.IsRunningBackend; set => State.Preferences.IsRunningBackend = value; }

        public int Latency { get => State.Preferences.Latency; set => State.Preferences.Latency = value; }

        public bool IsCustomAppSize { get => State.Preferences.IsCustomAppSize; set => State.Preferences.IsCustomAppSize = value; }

        public int AppWidth { get => State.Preferences.AppWidth; set => State.Preferences.AppWidth = value; }

        public int AppHeight { get => State.Preferences.AppHeight; set => State.Preferences.AppHeight = value; }

        public string DefaultEntryComboBoxTag { get => State.Preferences.DefaultEntryComboBoxTag; set => State.Preferences.DefaultEntryComboBoxTag = value; }

        public string DefaultPlayListComboBoxTag { get => State.Preferences.DefaultPlayListComboBoxTag; set => State.Preferences.DefaultPlayListComboBoxTag = value; }

        public ObservableCollection<BassOutputDevice> BassOutputDevices { get => State.Output.BassOutputDevices; set => State.Output.BassOutputDevices = value; }
        public BassOutputDevice SelectedDevice { get => State.Output.SelectedDevice; set => State.Output.SelectedDevice = value; }

        public string BackdropType { get => State.Preferences.BackdropType; set => State.Preferences.BackdropType = value; }

        public string ThemeType { get => State.Preferences.ThemeType; set => State.Preferences.ThemeType = value; }

        public bool IsDarkMode { get => State.Preferences.IsDarkMode; set => State.Preferences.IsDarkMode = value; }

        public EffectComboBoxItem Win2dTextEffectType { get => State.Preferences.Win2dTextEffectType; set => State.Preferences.Win2dTextEffectType = value; }

        public string Version
        {
            get => field;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool IsFolderWatchEnabled { get => State.Preferences.IsFolderWatchEnabled; set => State.Preferences.IsFolderWatchEnabled = value; }

        public ObservableCollection<FontInfo> FontFamilyList
        {
            get => field;
            set => SetProperty(ref field, value);
        }

        public FontInfo FontFamily { get => State.Preferences.FontFamily; set => State.Preferences.FontFamily = value; }

        public bool IsColorPickerVisible { get => State.Shell.IsColorPickerVisible; set => State.Shell.IsColorPickerVisible = value; }

        public Color CustomColor { get => State.Preferences.CustomColor; set => State.Preferences.CustomColor = value; }

        public bool IsCustomLyricsColorEnabled { get => State.Preferences.IsCustomLyricsColorEnabled; set => State.Preferences.IsCustomLyricsColorEnabled = value; }

        public Color LyricsCustomColor { get => State.Preferences.LyricsCustomColor; set => State.Preferences.LyricsCustomColor = value; }

        public double DesktopLyricsFontSize { get => State.Preferences.DesktopLyricsFontSize; set => State.Preferences.DesktopLyricsFontSize = value; }

        public FontInfo DesktopLyricsFontFamily { get => State.Preferences.DesktopLyricsFontFamily; set => State.Preferences.DesktopLyricsFontFamily = value; }

        public Color DesktopLyricsColor { get => State.Preferences.DesktopLyricsColor; set => State.Preferences.DesktopLyricsColor = value; }

        /// <summary>false（默认）= 桌面歌词颜色按悬浮窗周围环境自动取色（黑/白）；true = 用上方自选颜色覆盖自动取色。</summary>
        public bool IsDesktopLyricsCustomColorEnabled { get => State.Preferences.IsDesktopLyricsCustomColorEnabled; set => State.Preferences.IsDesktopLyricsCustomColorEnabled = value; }

        public bool IsDesktopLyricsTranslationEnabled { get => State.Preferences.IsDesktopLyricsTranslationEnabled; set => State.Preferences.IsDesktopLyricsTranslationEnabled = value; }

        public bool IsDesktopLyricsGlowEnabled { get => State.Preferences.IsDesktopLyricsGlowEnabled; set => State.Preferences.IsDesktopLyricsGlowEnabled = value; }

        public bool IsDesktopLyricsCharFloatEnabled { get => State.Preferences.IsDesktopLyricsCharFloatEnabled; set => State.Preferences.IsDesktopLyricsCharFloatEnabled = value; }

        public bool IsDesktopLyricsCharScaleEnabled { get => State.Preferences.IsDesktopLyricsCharScaleEnabled; set => State.Preferences.IsDesktopLyricsCharScaleEnabled = value; }

        /// <summary>长音节阈值（ms）：音节时长达到该值才触发发光/字缩动效（与主界面 LongSyllableThreshold 同语义）。</summary>
        public double DesktopLyricsLongSyllableThreshold { get => State.Preferences.DesktopLyricsLongSyllableThreshold; set => State.Preferences.DesktopLyricsLongSyllableThreshold = value; }

        /// <summary>发光强度（px，模糊半径）：0 无发光（与主界面 GlowAmount 同语义）。</summary>
        public double DesktopLyricsGlowAmount { get => State.Preferences.DesktopLyricsGlowAmount; set => State.Preferences.DesktopLyricsGlowAmount = value; }

        /// <summary>字浮强度（px，上浮距离）：0 无浮动（与主界面 CharFloatAmount 同语义）。</summary>
        public double DesktopLyricsCharFloatAmount { get => State.Preferences.DesktopLyricsCharFloatAmount; set => State.Preferences.DesktopLyricsCharFloatAmount = value; }

        /// <summary>字缩强度（%，长音节字符放大比例，110 = 1.1 倍）：与主界面 CharScaleAmount 同语义。</summary>
        public double DesktopLyricsCharScaleAmount { get => State.Preferences.DesktopLyricsCharScaleAmount; set => State.Preferences.DesktopLyricsCharScaleAmount = value; }

        /// <summary>兼容旧设置页绑定；状态和通知均转发到共享源。</summary>
        public bool IsDesktopLyricsKaraokeEnabled
        {
            get => State.DesktopLyrics.IsKaraokeEnabled;
            set => State.DesktopLyrics.IsKaraokeEnabled = value;
        }

        public int DesktopLyricsFontWeight { get => State.Preferences.DesktopLyricsFontWeight; set => State.Preferences.DesktopLyricsFontWeight = value; }

        /// <summary>字重 ComboBox 的 SelectedIndex（0=正常400 1=中等500 2=半粗600 3=粗体700）。</summary>
        public int DesktopLyricsWeightIndex
        {
            get => DesktopLyricsFontWeight switch { 500 => 1, 600 => 2, 700 => 3, _ => 0 };
            set => DesktopLyricsFontWeight = value switch { 1 => 500, 2 => 600, 3 => 700, _ => 400 };
        }

        /// <summary>阴影强度（%，0–100）：文字色反相软阴影；0 = 关闭（渲染直接跳过阴影），
        /// 50 = 单层满强度，100 = 双重叠加（Win2D 侧光晕约再深一倍，Composition 侧封顶不透明）。</summary>
        public double DesktopLyricsShadowAmount { get => State.Preferences.DesktopLyricsShadowAmount; set => State.Preferences.DesktopLyricsShadowAmount = value; }

        public int LyricsFontWeight { get => State.Preferences.LyricsFontWeight; set => State.Preferences.LyricsFontWeight = value; }

        /// <summary>主歌词字重 ComboBox 的 SelectedIndex（0=正常400 1=中等500 2=半粗600 3=粗体700）。</summary>
        public int LyricsFontWeightIndex
        {
            get => LyricsFontWeight switch { 400 => 0, 500 => 1, 600 => 2, _ => 3 };
            set => LyricsFontWeight = value switch { 0 => 400, 1 => 500, 2 => 600, _ => 700 };
        }

        internal void ScheduleDesktopLyricsStyleCommit()
            => App.Services.GetRequiredService<SettingsCoordinator>().ScheduleDesktopLyricsStyleCommit();

        public float CustomOpacity { get => State.Preferences.CustomOpacity; set => State.Preferences.CustomOpacity = value; }

        public bool IsUpdateBackDrop { get => State.Preferences.IsUpdateBackDrop; set => State.Preferences.IsUpdateBackDrop = value; }

        public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsAlignment { get => State.Preferences.LyricsAlignment; set => State.Preferences.LyricsAlignment = value; }

        public TextAlignment PlayingDetailAlignment { get => State.Preferences.PlayingDetailAlignment; set => State.Preferences.PlayingDetailAlignment = value; }

        public bool UsePlayingDetailAlignmentInPortrait { get => State.Preferences.UsePlayingDetailAlignmentInPortrait; set => State.Preferences.UsePlayingDetailAlignmentInPortrait = value; }

        public const double PortraitLyricsScale = 1.6;

        public bool IsPortraitLayout { get => State.Preferences.IsPortraitLayout; set => State.Preferences.IsPortraitLayout = value; }

        public TextAlignment EffectivePlayingDetailAlignment =>
            IsPortraitLayout && !UsePlayingDetailAlignmentInPortrait
                ? TextAlignment.Left
                : PlayingDetailAlignment;

        public bool IsMusicInfoVisible { get => State.Preferences.IsMusicInfoVisible; set => State.Preferences.IsMusicInfoVisible = value; }

        public bool IsGlobalFontSizeEnabled { get => State.Preferences.IsGlobalFontSizeEnabled; set => State.Preferences.IsGlobalFontSizeEnabled = value; }

        public double GlobalFontSize { get => State.Preferences.GlobalFontSize; set => State.Preferences.GlobalFontSize = value; }

        public double LyricsFontSize { get => State.Preferences.LyricsFontSize; set => State.Preferences.LyricsFontSize = value; }

        public string MusicCoverCache { get => State.Preferences.MusicCoverCache; set => State.Preferences.MusicCoverCache = value; }

        public bool IsDopEnabled
        {
            get => State.Preferences.Audio.IsDopEnabled;
            set
            {
                if (!DsdBitstreamAllowed)
                {
                    // 试用受限：拒绝写入并通知绑定回弹，保持用户原有偏好（购买后自动恢复生效）。
                    OnPropertyChanged(nameof(IsDopEnabled));
                    return;
                }
                SetAudioPreference(nameof(IsDopEnabled), value);
            }
        }

        public bool ExperimentalSurround51
        {
            get => State.Preferences.Audio.ExperimentalSurround51;
            set
            {
                if (value && !Surround51Allowed)
                {
                    OnPropertyChanged(nameof(ExperimentalSurround51));
                    return;
                }
                SetAudioPreference(nameof(ExperimentalSurround51), value);
            }
        }

        public bool ExperimentalAtmosPassthrough
        {
            get => State.Preferences.Audio.ExperimentalAtmosPassthrough;
            set
            {
                if (value && !AtmosPassthroughAllowed)
                {
                    OnPropertyChanged(nameof(ExperimentalAtmosPassthrough));
                    return;
                }
                SetAudioPreference(nameof(ExperimentalAtmosPassthrough), value);
            }
        }

        private bool SetAudioPreference(string name, bool value)
        {
            var audio = State.Preferences.Audio;
            switch (name)
            {
                case nameof(IsDopEnabled):
                    if (audio.IsDopEnabled == value) return false;
                    audio.IsDopEnabled = value;
                    break;
                case nameof(ExperimentalSurround51):
                    if (audio.ExperimentalSurround51 == value) return false;
                    audio.ExperimentalSurround51 = value;
                    break;
                case nameof(ExperimentalAtmosPassthrough):
                    if (audio.ExperimentalAtmosPassthrough == value) return false;
                    audio.ExperimentalAtmosPassthrough = value;
                    break;
            }
            return true;
        }

        public bool IsFadeEnabled { get => State.Preferences.IsFadeEnabled; set => State.Preferences.IsFadeEnabled = value; }
        public ObservableCollection<int> DsdPcmFreqs
        {
            get => field;
            set => SetProperty(ref field, value);
        } = [44100, 88200, 176400, 352800];

        public int DsdPcmFreq { get => State.Preferences.DsdPcmFreq; set => State.Preferences.DsdPcmFreq = value; }

        public float LyricsBlurAmount { get => State.Preferences.LyricsBlurAmount; set => State.Preferences.LyricsBlurAmount = value; }

        public double CharFloatAmount { get => State.Preferences.CharFloatAmount; set => State.Preferences.CharFloatAmount = value; }

        public double CharScaleAmount { get => State.Preferences.CharScaleAmount; set => State.Preferences.CharScaleAmount = value; }

        public double GlowAmount { get => State.Preferences.GlowAmount; set => State.Preferences.GlowAmount = value; }

        public double LongSyllableThreshold { get => State.Preferences.LongSyllableThreshold; set => State.Preferences.LongSyllableThreshold = value; }

        public double PlayingLineTopOffsetPercent { get => State.Preferences.PlayingLineTopOffsetPercent; set => State.Preferences.PlayingLineTopOffsetPercent = value; }

        public double TranslatedOpacityPercent { get => State.Preferences.TranslatedOpacityPercent; set => State.Preferences.TranslatedOpacityPercent = value; }

        public double UnplayedOpacityPercent { get => State.Preferences.UnplayedOpacityPercent; set => State.Preferences.UnplayedOpacityPercent = value; }

        public double TargetFrameRate { get => State.Preferences.TargetFrameRate; set => State.Preferences.TargetFrameRate = value; }

        public bool EnableAdvancedLyricsEffect { get => State.Preferences.EnableAdvancedLyricsEffect; set => State.Preferences.EnableAdvancedLyricsEffect = value; }

        public EasingType ScrollEasingType { get => State.Preferences.ScrollEasingType; set => State.Preferences.ScrollEasingType = value; }
        public int ScrollEasingTypeIndex
        {
            get => Math.Clamp((int)ScrollEasingType, 0, MaxEasingTypeIndex);
            set => ScrollEasingType = (EasingType)Math.Clamp(value, 0, MaxEasingTypeIndex);
        }

        private static readonly int MaxEasingTypeIndex = (int)EasingType.FlowWave; // 枚举尾成员

        public EaseMode ScrollEasingMode { get => State.Preferences.ScrollEasingMode; set => State.Preferences.ScrollEasingMode = value; }
        public int ScrollEasingModeIndex
        {
            get => Math.Clamp((int)ScrollEasingMode, 0, MaxEaseModeIndex);
            set => ScrollEasingMode = (EaseMode)Math.Clamp(value, 0, MaxEaseModeIndex);
        }

        private static readonly int MaxEaseModeIndex = (int)EaseMode.FlowWave; // 枚举尾成员

        public bool EnableGlobalHotKey { get => State.Preferences.EnableGlobalHotKey; set => State.Preferences.EnableGlobalHotKey = value; }

        public bool IsTrimOnHideEnabled { get => State.Preferences.IsTrimOnHideEnabled; set => State.Preferences.IsTrimOnHideEnabled = value; }

        public bool IsTrimAfterPlaybackEnabled { get => State.Preferences.IsTrimAfterPlaybackEnabled; set => State.Preferences.IsTrimAfterPlaybackEnabled = value; }

        public bool HasGlobalHotKeyConflict => State.HotKeys.HasGlobalHotKeyConflict;
        public string GlobalHotKeyConflictTitle => State.HotKeys.GlobalHotKeyConflictTitle;
        public List<string> PlayOrPauseShortcut { get => State.HotKeys.PlayOrPauseShortcut; set => State.HotKeys.PlayOrPauseShortcut = value; }
        public List<string> NextSongShortcut { get => State.HotKeys.NextSongShortcut; set => State.HotKeys.NextSongShortcut = value; }
        public List<string> PreviousSongShortcut { get => State.HotKeys.PreviousSongShortcut; set => State.HotKeys.PreviousSongShortcut = value; }
        public List<string> VolumeUpShortcut { get => State.HotKeys.VolumeUpShortcut; set => State.HotKeys.VolumeUpShortcut = value; }
        public List<string> VolumeDownShortcut { get => State.HotKeys.VolumeDownShortcut; set => State.HotKeys.VolumeDownShortcut = value; }
        public List<string> TogglePlayingDetailShortcut { get => State.HotKeys.TogglePlayingDetailShortcut; set => State.HotKeys.TogglePlayingDetailShortcut = value; }
        public List<string> BackShortcut { get => State.HotKeys.BackShortcut; set => State.HotKeys.BackShortcut = value; }
        public List<string> ShowWindowShortcut { get => State.HotKeys.ShowWindowShortcut; set => State.HotKeys.ShowWindowShortcut = value; }
        public List<string> ToggleFullScreenShortcut { get => State.HotKeys.ToggleFullScreenShortcut; set => State.HotKeys.ToggleFullScreenShortcut = value; }
        public List<string> ToggleDesktopLyricsShortcut { get => State.HotKeys.ToggleDesktopLyricsShortcut; set => State.HotKeys.ToggleDesktopLyricsShortcut = value; }
        public List<string> ToggleDesktopLyricsLockShortcut { get => State.HotKeys.ToggleDesktopLyricsLockShortcut; set => State.HotKeys.ToggleDesktopLyricsLockShortcut = value; }
        public List<string> ToggleDesktopLyricsKaraokeShortcut { get => State.HotKeys.ToggleDesktopLyricsKaraokeShortcut; set => State.HotKeys.ToggleDesktopLyricsKaraokeShortcut = value; }
        public List<string> ResetDesktopLyricsShortcut { get => State.HotKeys.ResetDesktopLyricsShortcut; set => State.HotKeys.ResetDesktopLyricsShortcut = value; }
        public void InitHotKeys() => App.Services.GetRequiredService<HotKeyService>().Refresh();

        public List<double> TargetFrameRateOptions { get; } = [60, 72, 80, 90, 120, 144, 160, 165, 180, 240, 280, 320, 360, 480];

        public ObservableCollection<EffectComboBoxItem> TextEffectItems =
        [
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextDefaultEffect"), Value = AnimatedTextEffect.TextDefaultEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextElasticEffect"), Value = AnimatedTextEffect.TextElasticEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextZoomEffect"), Value = AnimatedTextEffect.TextZoomEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextBlurEffect"), Value = AnimatedTextEffect.TextBlurEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextMotionBlurEffect"), Value = AnimatedTextEffect.TextMotionBlurEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextPivotEffect"), Value = AnimatedTextEffect.TextPivotEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextFadeEffect"), Value = AnimatedTextEffect.TextFadeEffect },
            new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextWipeEffect"), Value = AnimatedTextEffect.TextWipeEffect },

        ];

        public Task GetWasapiDeviceAsync() => App.Services.GetRequiredService<OutputDeviceService>().RefreshAsync();

        public IRelayCommand<string> BackdropTypeChangedCommand => App.Services.GetRequiredService<SettingsActions>().BackdropTypeChangedCommand;
        public IRelayCommand<string> ThemeTypeChangedCommand => App.Services.GetRequiredService<SettingsActions>().ThemeTypeChangedCommand;
        public IAsyncRelayCommand OpenLogPathCommand => App.Services.GetRequiredService<SettingsActions>().OpenLogPathCommand;
        public IAsyncRelayCommand OpenSettingsFolderCommand => App.Services.GetRequiredService<SettingsActions>().OpenSettingsFolderCommand;
        public IAsyncRelayCommand ChangeCoverCacheLocationCommand => App.Services.GetRequiredService<SettingsActions>().ChangeCoverCacheLocationCommand;
        public IRelayCommand OpenWebSiteCommand => App.Services.GetRequiredService<SettingsActions>().OpenWebSiteCommand;
        public IRelayCommand OpenMainGitHubCommand => App.Services.GetRequiredService<SettingsActions>().OpenMainGitHubCommand;
        public IAsyncRelayCommand OpenCoverCacheLocationCommand => App.Services.GetRequiredService<SettingsActions>().OpenCoverCacheLocationCommand;
        public IAsyncRelayCommand ClearCoverCacheCommand => App.Services.GetRequiredService<SettingsActions>().ClearCoverCacheCommand;
        public IAsyncRelayCommand TrimNowCommand => App.Services.GetRequiredService<SettingsActions>().TrimNowCommand;
        public IRelayCommand ResetWindowBoundsCommand => App.Services.GetRequiredService<SettingsActions>().ResetWindowBoundsCommand;
    }
}
