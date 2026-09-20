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
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        public bool IsRealDevceChange { get; set; } = true;
        private bool _isLoadingDevices;
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

        public bool IsHoverScrollEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value) && IsInitialized)
                    _ = _musicDatabaseService.SaveSettingAsync();
            }
        } = true;

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

        public string DefaultEntryComboBoxTag
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnDefaultEntryComboBoxTagChanged(value);
                }
            }
        } = "AddFolder";

        public string DefaultPlayListComboBoxTag { get => State.Preferences.DefaultPlayListComboBoxTag; set => State.Preferences.DefaultPlayListComboBoxTag = value; }

        public ObservableCollection<BassOutputDevice> BassOutputDevices
        {
            get => field;
            set => SetProperty(ref field, value);
        } = new();

        public BassOutputDevice SelectedDevice
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (value is not null)
                    {
                        if (IsRealDevceChange)
                        {
                            if (IsInitialized)
                            {
                                if (value.OutputMode != "ASIO")
                                {
                                    AppSettings.BassOutputDeviceId = value.Id;
                                    AppSettings.WasapiEndpointId = value.EndpointId;
                                }
                                else
                                {
                                    AppSettings.BassASIODeviceId = value.AsioId;
                                }
                                AppSettings.DeviceName = value.Name;
                                AppSettings.OutputMode = value.OutputMode;
                                _ = _musicDatabaseService.SaveSettingAsync();
                                AppSettings.OnOutputSettingsChanged();
                            }
                        }
                        else
                        {
                            IsRealDevceChange = true;
                        }
                    }
                }
            }
        }

        public string BackdropType { get => State.Preferences.BackdropType; set => State.Preferences.BackdropType = value; }

        public string ThemeType
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.AppTheme = value;
                    try
                    {
                        switch (value)
                        {
                            case "Default":
                                IsDarkMode = !ToolUtils.GetIsLightTheme();
                                AppSettings.ElementTheme = ElementTheme.Default;
                                break;
                            case "Dark":
                                IsDarkMode = true;
                                AppSettings.ElementTheme = ElementTheme.Dark;
                                break;
                            case "Light":
                                IsDarkMode = false;
                                AppSettings.ElementTheme = ElementTheme.Light;
                                break;
                            default:
                                IsDarkMode = !ToolUtils.GetIsLightTheme();
                                AppSettings.ElementTheme = ElementTheme.Default;
                                break;
                        }
                        App.MainWindow?.SetAppTheme();
                        if (IsInitialized)
                        {
                            App.Services.GetRequiredService<MusicBrowseViewModel>().ThemeChangedUpdateCover();
                            _ = _musicDatabaseService.SaveSettingAsync();
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, ex.Message);
                    }
                }
            }
        } = "Default";

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

        public bool IsColorPickerVisible
        {
            get => field;
            set => SetProperty(ref field, value);
        } = false;

        public Color CustomColor { get => State.Preferences.CustomColor; set => State.Preferences.CustomColor = value; }

        public bool IsCustomLyricsColorEnabled { get => State.Preferences.IsCustomLyricsColorEnabled; set => State.Preferences.IsCustomLyricsColorEnabled = value; }

        public Color LyricsCustomColor { get => State.Preferences.LyricsCustomColor; set => State.Preferences.LyricsCustomColor = value; }

        public double DesktopLyricsFontSize { get => State.Preferences.DesktopLyricsFontSize; set => State.Preferences.DesktopLyricsFontSize = value; }

        public FontInfo DesktopLyricsFontFamily
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value) && value is not null)
                {
                    AppSettings.DesktopLyricsFontFamily = value.FontFamily.Source;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        }

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
            get => field;
            set
            {
                if (!DsdBitstreamAllowed)
                {
                    // 试用受限：拒绝写入并通知绑定回弹，保持用户原有偏好（购买后自动恢复生效）。
                    OnPropertyChanged(nameof(IsDopEnabled));
                    return;
                }
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        public bool ExperimentalSurround51
        {
            get => field;
            set
            {
                if (value && !Surround51Allowed)
                {
                    OnPropertyChanged(nameof(ExperimentalSurround51));
                    return;
                }
                if (SetProperty(ref field, value) && IsInitialized)
                {
                    _ = _musicDatabaseService.SaveSettingAsync();
                    AppSettings.OnOutputSettingsUpdated();
                }
            }
        }

        public bool ExperimentalAtmosPassthrough
        {
            get => field;
            set
            {
                if (value && !AtmosPassthroughAllowed)
                {
                    OnPropertyChanged(nameof(ExperimentalAtmosPassthrough));
                    return;
                }
                if (SetProperty(ref field, value) && IsInitialized)
                {
                    _ = _musicDatabaseService.SaveSettingAsync();
                    AppSettings.OnOutputSettingsUpdated();
                }
            }
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

        public bool HasGlobalHotKeyConflict
        {
            get => field;
            private set => SetProperty(ref field, value);
        } = false;

        public string GlobalHotKeyConflictTitle
        {
            get => field;
            private set => SetProperty(ref field, value);
        } = string.Empty;

        private void OnGlobalHotKeyConflictsChanged(object? sender, EventArgs e)
        {
            bool any = GlobalHotKeyHook.Conflicts.Count > 0;
            if (HasGlobalHotKeyConflict != any)
            {
                HasGlobalHotKeyConflict = any;
            }
            if (any)
            {
                string format = ToolUtils.GetString("GlobalHotKeyConflictTitleFormat");
                string list = string.Join(", ", GlobalHotKeyHook.Conflicts.Select(GlobalHotKeyHook.GetDisplayName));
                GlobalHotKeyConflictTitle = string.Format(format, list);
            }
            else
            {
                GlobalHotKeyConflictTitle = string.Empty;
            }
        }

        public List<string> PlayOrPauseShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.PlayOrPauseSong, value, () =>
                            {
                                App.Services.GetRequiredService<PlaybackCommands>().ToggleCommand.Execute(null);
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "P" };

        public List<string> NextSongShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.NextSong, value, () =>
                            {
                                App.Services.GetRequiredService<PlaybackCommands>().NextCommand.Execute(null);
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "Right" };

        public List<string> PreviousSongShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.PreviousSong, value, () =>
                            {
                                App.Services.GetRequiredService<PlaybackCommands>().PreviousCommand.Execute(null);
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "Left" };

        public List<string> VolumeUpShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.VolumeUp, value, () =>
                            {
                                AdjustVolume(5);
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "Up" };

        public List<string> VolumeDownShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.VolumeDown, value, () =>
                            {
                                AdjustVolume(-5);
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "Down" };

        public List<string> TogglePlayingDetailShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.TogglePlayingDetail, value, () =>
                            {
                                if (App.MainWindow is not { Visible: true }) return;
                                var mainPage = App.Services.GetRequiredService<MainPage>();
                                if (mainPage.IsPlayingDetailVisible)
                                    mainPage.NavigatebackToMusicBrowsePage();
                                else
                                    mainPage.NavigateToPlayingDetailPage();
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "Q" };

        public List<string> BackShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.Back, value, () =>
                            {
                                if (App.MainWindow is not { Visible: true }) return;
                                App.Services.GetRequiredService<MainPage>().HandleBackNavigation();
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "B" };

        public List<string> ShowWindowShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ShowWindow, value, () =>
                            {
                                App.MainWindow?.ToggleShowHide();
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "W" };

        public List<string> ToggleFullScreenShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ToggleFullScreen, value, () =>
                            {
                                if (App.MainWindow is not { Visible: true }) return;
                                ToggleFullScreen();
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "F" };

        public List<string> ToggleDesktopLyricsShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ToggleDesktopLyrics, value, () =>
                            {
                                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                                desktopLyrics.IsEnabled = !desktopLyrics.IsEnabled;
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "D" };

        public List<string> ToggleDesktopLyricsLockShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ToggleDesktopLyricsLock, value, () =>
                            {
                                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                                desktopLyrics.IsLocked = !desktopLyrics.IsLocked;
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "L" };

        public List<string> ToggleDesktopLyricsKaraokeShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ToggleDesktopLyricsKaraoke, value, () =>
                            {
                                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                                desktopLyrics.IsKaraokeEnabled = !desktopLyrics.IsKaraokeEnabled;
                            });
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "K" };

        public List<string> ResetDesktopLyricsShortcut
        {
            get;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        if (EnableGlobalHotKey)
                        {
                            GlobalHotKeyHook.UpdateHotKey(App.MainWindow, ShortcutId.ResetDesktopLyrics, value, DesktopLyricsManager.ResetWindowBounds);
                        }
                    }
                }
            }
        } = new List<string> { "Ctrl", "Alt", "R" };

        public void InitHotKeys()
        {
            if (_isDisposed || _lifecycle.Phase == AppPhase.Stopping) return;
            var window = App.MainWindow;
            if (window is null) return;

            GlobalHotKeyHook.ConflictsChanged -= OnGlobalHotKeyConflictsChanged;
            GlobalHotKeyHook.ConflictsChanged += OnGlobalHotKeyConflictsChanged;
            GlobalHotKeyHook.ClearAll(window);

            if (!EnableGlobalHotKey) return;

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PlayOrPauseSong, PlayOrPauseShortcut, () =>
            {
                App.Services.GetRequiredService<PlaybackCommands>().ToggleCommand.Execute(null);
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.NextSong, NextSongShortcut, () =>
            {
                App.Services.GetRequiredService<PlaybackCommands>().NextCommand.Execute(null);
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PreviousSong, PreviousSongShortcut, () =>
            {
                App.Services.GetRequiredService<PlaybackCommands>().PreviousCommand.Execute(null);
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.VolumeUp, VolumeUpShortcut, () =>
            {
                AdjustVolume(5);
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.VolumeDown, VolumeDownShortcut, () =>
            {
                AdjustVolume(-5);
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.TogglePlayingDetail, TogglePlayingDetailShortcut, () =>
            {
                if (window is not { Visible: true }) return;
                var mainPage = App.Services.GetRequiredService<MainPage>();
                if (mainPage.IsPlayingDetailVisible)
                    mainPage.NavigatebackToMusicBrowsePage();
                else
                    mainPage.NavigateToPlayingDetailPage();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.Back, BackShortcut, () =>
            {
                if (window is not { Visible: true }) return;
                App.Services.GetRequiredService<MainPage>().HandleBackNavigation();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ShowWindow, ShowWindowShortcut, () =>
            {
                window.ToggleShowHide();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleFullScreen, ToggleFullScreenShortcut, () =>
            {
                if (App.MainWindow is not { Visible: true }) return;
                ToggleFullScreen();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyrics, ToggleDesktopLyricsShortcut, () =>
            {
                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                desktopLyrics.IsEnabled = !desktopLyrics.IsEnabled;
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyricsLock, ToggleDesktopLyricsLockShortcut, () =>
            {
                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                desktopLyrics.IsLocked = !desktopLyrics.IsLocked;
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ToggleDesktopLyricsKaraoke, ToggleDesktopLyricsKaraokeShortcut, () =>
            {
                var desktopLyrics = App.Services.GetRequiredService<DesktopLyricsViewModel>();
                desktopLyrics.IsKaraokeEnabled = !desktopLyrics.IsKaraokeEnabled;
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.ResetDesktopLyrics, ResetDesktopLyricsShortcut, DesktopLyricsManager.ResetWindowBounds);
        }

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

        public async Task GetWasapiDeviceAsync()
        {
            if (_isLoadingDevices) return;
            _isLoadingDevices = true;
            try
            {
                BassOutputDevices.Clear();
                //默认设备
                BassOutputDevices.Add(new BassOutputDevice
                {
                    Name = "DefaultDevice",
                    Tag = ToolUtils.GetString("DefaultDevice") + " [DirectSound]",
                    Id = -1,
                    OutputMode = "DirectSound"
                });
                BassOutputDevices.Add(new BassOutputDevice
                {
                    Name = "DefaultDevice",
                    Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiSharedText")}]",
                    Id = -1,
                    OutputMode = "WasapiShared"
                });
                BassOutputDevices.Add(new BassOutputDevice
                {
                    Name = "DefaultDevice",
                    Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                    Id = -1,
                    OutputMode = "WasapiExclusivePush"
                });
                BassOutputDevices.Add(new BassOutputDevice
                {
                    Name = "DefaultDevice",
                    Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                    Id = -1,
                    OutputMode = "WasapiExclusiveEvent"
                });

                var cmd = App.Services.GetRequiredService<BassPlayerCommandService>();

                // ASIO devices from server
                var asioDevices = await cmd.GetAsioDevices();
                foreach (var (id, name) in asioDevices)
                {
                    BassOutputDevices.Add(new BassOutputDevice
                    {
                        Name = name,
                        Tag = name + " [ASIO]",
                        AsioId = id,
                        OutputMode = "ASIO"
                    });
                }

                // WASAPI devices from server
                var wasapiDevices = await cmd.GetWasapiDevices();
                foreach (var (id, name) in wasapiDevices)
                {
                    if (!BassOutputDevices.AsValueEnumerable().Any(d => d.EndpointId == cmd.GetWasapiEndpointId(id) && d.OutputMode == "WasapiShared"))
                    {
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiSharedText")}]",
                            Id = id,
                            EndpointId = cmd.GetWasapiEndpointId(id),
                            OutputMode = "WasapiShared"
                        });
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                            Id = id,
                            EndpointId = cmd.GetWasapiEndpointId(id),
                            OutputMode = "WasapiExclusivePush"
                        });
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                            Id = id,
                            EndpointId = cmd.GetWasapiEndpointId(id),
                            OutputMode = "WasapiExclusiveEvent"
                        });
                    }
                }

                var device = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.OutputMode == AppSettings.OutputMode && (string.IsNullOrEmpty(AppSettings.WasapiEndpointId)
                    || d.OutputMode == "ASIO" || d.OutputMode == "DirectSound" ? d.Name == AppSettings.DeviceName : d.EndpointId == AppSettings.WasapiEndpointId));
                if (device is null)
                {
                    // 枚举不到已保存设备（未上电/驱动未就绪等瞬时原因）时只回退内存状态到默认设备，
                    // 不触发落盘，避免把用户保存的输出设备设置永久重置（下次启动设备在位时自动恢复）
                    AppSettings.OutputMode = "DirectSound";
                    AppSettings.BassOutputDeviceId = -1;
                    AppSettings.WasapiEndpointId = null;
                    AppSettings.DeviceName = "DefaultDevice";
                    IsRealDevceChange = false;
                    SelectedDevice = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == "DefaultDevice" && d.OutputMode == "DirectSound");
                    AppSettings.OnOutputSettingsChanged();
                }
                else
                {
                    // 启动/刷新枚举时回选已保存设备不算真实切换：跳过落盘（避免多余全量写盘），
                    // 但保留输出重配事件以维持原有启动初始化行为
                    IsRealDevceChange = false;
                    SelectedDevice = device;
                    if (device.OutputMode.StartsWith("Wasapi", StringComparison.Ordinal))
                    {
                        AppSettings.BassOutputDeviceId = device.Id;
                        AppSettings.WasapiEndpointId = device.EndpointId;
                    }
                }
            }
            finally { RefreshAtmosDevices(); _isLoadingDevices = false; }
        }

        [RelayCommand]
        private void OnBackdropTypeChanged(string type)
        {
            try
            {
                switch (type)
                {
                    case "Acrylic":
                        BackdropType = "Acrylic";
                        IsColorPickerVisible = false;
                        break;
                    case "TransparentAcrylic":
                        BackdropType = "TransparentAcrylic";
                        IsColorPickerVisible = false;
                        break;
                    case "Mica":
                        BackdropType = "Mica";
                        IsColorPickerVisible = false;
                        break;
                    case "TransparentTint":
                        BackdropType = "TransparentTint";
                        IsColorPickerVisible = false;
                        break;
                    case "CustomAcrylicStyle":
                        BackdropType = "CustomAcrylicStyle";
                        IsColorPickerVisible = true;
                        break;
                }
                App.MainWindow?.SetAppStyle();
                if (IsInitialized)
                {
                    _ = _musicDatabaseService.SaveSettingAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, ex.Message);
            }
        }
        [RelayCommand]
        private void OnThemeTypeChanged(string type)
        {
            ThemeType = type;
        }

        private void OnDefaultEntryComboBoxTagChanged(string value)
        {
            if (IsInitialized)
            {
                _ = _musicDatabaseService.SaveSettingAsync();
            }
        }

        [RelayCommand]
        private async Task OpenLogPath()
        {
            var logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Logs");
            var folder = await StorageFolder.GetFolderFromPathAsync(logDirectory);
            var options = new FolderLauncherOptions
            {
                DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
            };
            await Launcher.LaunchFolderAsync(folder, options);
        }

        [RelayCommand]
        private async Task OpenSettingsFolder()
        {
            string settingsDirectory;
            try
            {
                settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "OriginalSoundPlayer", "Settings");
                if (!Directory.Exists(settingsDirectory))
                {
                    Directory.CreateDirectory(settingsDirectory);
                }
            }
            catch
            {
                settingsDirectory = ApplicationData.Current.LocalFolder.Path;
            }
            var folder = await StorageFolder.GetFolderFromPathAsync(settingsDirectory);
            var options = new FolderLauncherOptions
            {
                DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
            };
            await Launcher.LaunchFolderAsync(folder, options);
        }

        [RelayCommand]
        private async Task ChangeCoverCacheLocation()
        {
            var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
            PickFolderResult folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                MusicCoverCache = folder.Path;
            }
        }

        [RelayCommand]
        private void OpenWebSite()
        {
            _ = Launcher.LaunchUriAsync(new Uri("https://johnwikix.github.io/original-sound-player-page"));
        }

        [RelayCommand]
        private void OpenMainGitHub()
        {
            _ = Launcher.LaunchUriAsync(new Uri("https://github.com/Johnwikix/original-sound-hq-player"));
        }
        [RelayCommand]
        private async Task OpenCoverCacheLocation()
        {
            var folder = await StorageFolder.GetFolderFromPathAsync(MusicCoverCache);
            var options = new FolderLauncherOptions
            {
                DesiredRemainingView = Windows.UI.ViewManagement.ViewSizePreference.UseMore
            };
            await Launcher.LaunchFolderAsync(folder, options);
        }

        [RelayCommand]
        private async Task ClearCoverCache()
        {
            try
            {
                string cacheRoot = MusicCoverCache;
                await Task.Run(() =>
                {
                    // 根目录下的 .bin 为网络封面原图缓存
                    if (!string.IsNullOrEmpty(cacheRoot) && Directory.Exists(cacheRoot))
                    {
                        foreach (var file in Directory.EnumerateFiles(cacheRoot, "*.bin"))
                        {
                            File.Delete(file);
                        }
                    }

                    // Cache 子目录存放缩略图 .bmp 与全尺寸 _raw.bin，整体删除
                    if (!string.IsNullOrEmpty(cacheRoot))
                    {
                        var cacheDir = Path.Combine(cacheRoot, "Cache");
                        if (Directory.Exists(cacheDir))
                        {
                            Directory.Delete(cacheDir, recursive: true);
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"清空封面缓存失败: {ex.Message}");
            }
        }

        [RelayCommand]
        private static async Task TrimNow()
        {
            await WorkingSetCompressor.TrimSelfAsync();
        }

        [RelayCommand]
        private void ResetWindowBounds()
        {
            App.MainWindow.CenterOnScreen();
        }
    }
}
