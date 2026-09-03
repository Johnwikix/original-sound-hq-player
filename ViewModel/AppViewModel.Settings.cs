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
        public bool EnableLightWave
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;
        public AnimatedWin2dControls.Impressionist.PaletteAlgorithm PaletteAlgorithm
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(PaletteAlgorithmIndex));
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = AnimatedWin2dControls.Impressionist.PaletteAlgorithm.KMeansPP;
        public int PaletteAlgorithmIndex
        {
            get => (int)PaletteAlgorithm;
            set => PaletteAlgorithm = (AnimatedWin2dControls.Impressionist.PaletteAlgorithm)value;
        }
        public AnimatedWin2dControls.BackgroundShaderMode BackgroundShader
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(BackgroundShaderIndex));
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = AnimatedWin2dControls.BackgroundShaderMode.FluidBackground;
        public int BackgroundShaderIndex
        {
            get => (int)BackgroundShader;
            set => BackgroundShader = (AnimatedWin2dControls.BackgroundShaderMode)value;
        }
        public int CoverSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    CoverLoadQueue.CoverSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 0;

        public bool IsWin2dCoverImageControlEnable
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public bool IsWin2dAnimatedText
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public int DsdGain
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        } = 6;

        public bool IsAutoLyricsEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsAutoLyricsEnabled = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        public string ArtistSplitSymbols
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.ArtistSplitSymbols = value ?? string.Empty;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        RefreshAllViews();
                    }
                }
            }
        } = AppSettings.ArtistSplitSymbols;

        public bool IsAutoCoverEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsAutoCoverEnabled = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        public bool IsRunningBackend
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsRunningBackend = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        public int Latency
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 300;

        public bool IsCustomAppSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsCustomAppSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public int AppWidth
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.AppWidth = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 1440;

        public int AppHeight
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.AppHeight = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 810;

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

        public string DefaultPlayListComboBoxTag
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = "song";

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

        public string BackdropType
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.AppStyle = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = "TransparentAcrylic";

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

        public bool IsDarkMode
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                        SendLyricsSettings();
                }
            }
        } = false;

        public EffectComboBoxItem Win2dTextEffectType
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = new EffectComboBoxItem { DisplayName = ToolUtils.GetString("TextDefaultEffect"), Value = AnimatedTextEffect.TextDefaultEffect };

        public string Version
        {
            get => field;
            set => SetProperty(ref field, value);
        } = string.Empty;

        public bool IsFolderWatchEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        public ObservableCollection<FontInfo> FontFamilyList
        {
            get => field;
            set => SetProperty(ref field, value);
        }

        public FontInfo FontFamily
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsSettings();
                    }
                }
            }
        }

        public bool IsColorPickerVisible
        {
            get => field;
            set => SetProperty(ref field, value);
        } = false;

        public Color CustomColor
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.CustomColorArgb = (uint)((value.A << 24) | (value.R << 16) | (value.G << 8) | value.B);
                    if (IsInitialized)
                    {
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = Color.FromArgb(0xFF, 0x80, 0x80, 0x80);

        public bool IsCustomLyricsColorEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = false;

        public Color LyricsCustomColor
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.LyricsCustomColorRgb = (uint)((value.R << 16) | (value.G << 8) | value.B);
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        public double DesktopLyricsFontSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsFontSize = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 36;

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

        public Color DesktopLyricsColor
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsColorRgb = (uint)((value.R << 16) | (value.G << 8) | value.B);
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>false（默认）= 桌面歌词颜色按悬浮窗周围环境自动取色（黑/白）；true = 用上方自选颜色覆盖自动取色。</summary>
        public bool IsDesktopLyricsCustomColorEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsDesktopLyricsCustomColorEnabled = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = false;

        public bool IsDesktopLyricsTranslationEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsDesktopLyricsTranslationEnabled = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = true;

        public bool IsDesktopLyricsGlowEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsDesktopLyricsGlowEnabled = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = true;

        public bool IsDesktopLyricsCharFloatEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsDesktopLyricsCharFloatEnabled = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = true;

        public bool IsDesktopLyricsCharScaleEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsDesktopLyricsCharScaleEnabled = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = true;

        /// <summary>长音节阈值（ms）：音节时长达到该值才触发发光/字缩动效（与主界面 LongSyllableThreshold 同语义）。</summary>
        public double DesktopLyricsLongSyllableThreshold
        {
            get => field;
            set
            {
                value = Math.Clamp(value, 0, 5000);
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsLongSyllableThreshold = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 700.0;

        /// <summary>发光强度（px，模糊半径）：0 无发光（与主界面 GlowAmount 同语义）。</summary>
        public double DesktopLyricsGlowAmount
        {
            get => field;
            set
            {
                value = Math.Clamp(value, 0, 10);
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsGlowAmount = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 5.0;

        /// <summary>字浮强度（px，上浮距离）：0 无浮动（与主界面 CharFloatAmount 同语义）。</summary>
        public double DesktopLyricsCharFloatAmount
        {
            get => field;
            set
            {
                value = Math.Clamp(value, 0, 10);
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsCharFloatAmount = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 5.0;

        /// <summary>字缩强度（%，长音节字符放大比例，110 = 1.1 倍）：与主界面 CharScaleAmount 同语义。</summary>
        public double DesktopLyricsCharScaleAmount
        {
            get => field;
            set
            {
                value = Math.Clamp(value, 50, 150);
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsCharScaleAmount = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 110.0;

        /// <summary>逐字效果开关（设置页绑定入口）：field-backed 可观察属性保证 OneWay 绑定联动；
        /// 转发给桌面歌词 VM 单例（其 setter 负责持久化与窗口热切换通知），不经样式防抖提交。</summary>
        public bool IsDesktopLyricsKaraokeEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    App.Services.GetRequiredService<DesktopLyricsViewModel>().IsKaraokeEnabled = value;
                }
            }
        } = false;

        public int DesktopLyricsFontWeight
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsFontWeight = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 400;

        /// <summary>字重 ComboBox 的 SelectedIndex（0=正常400 1=中等500 2=半粗600 3=粗体700）。</summary>
        public int DesktopLyricsWeightIndex
        {
            get => DesktopLyricsFontWeight switch { 500 => 1, 600 => 2, 700 => 3, _ => 0 };
            set => DesktopLyricsFontWeight = value switch { 1 => 500, 2 => 600, 3 => 700, _ => 400 };
        }

        /// <summary>阴影强度（%，0–100）：文字色反相软阴影；0 = 关闭（渲染直接跳过阴影），
        /// 50 = 单层满强度，100 = 双重叠加（Win2D 侧光晕约再深一倍，Composition 侧封顶不透明）。</summary>
        public double DesktopLyricsShadowAmount
        {
            get => field;
            set
            {
                value = Math.Clamp(value, 0, 100);
                if (SetProperty(ref field, value))
                {
                    AppSettings.DesktopLyricsShadowAmount = value;
                    if (IsInitialized)
                    {
                        ScheduleDesktopLyricsStyleCommit();
                    }
                }
            }
        } = 50.0;

        public int LyricsFontWeight
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.LyricsFontWeight = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsSettings();
                    }
                }
            }
        } = 700;

        /// <summary>主歌词字重 ComboBox 的 SelectedIndex（0=正常400 1=中等500 2=半粗600 3=粗体700）。</summary>
        public int LyricsFontWeightIndex
        {
            get => LyricsFontWeight switch { 400 => 0, 500 => 1, 600 => 2, _ => 3 };
            set => LyricsFontWeight = value switch { 0 => 400, 1 => 500, 2 => 600, _ => 700 };
        }

        // 不要在本文件加 using Microsoft.UI.Dispatching —— 会触发 XAML 编译器源码扫描的
        // WMC9999 内部错误（AppViewModel.cs 中的同款 using 不受影响），故用全限定类型名。
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _desktopLyricsStyleCommitTimer;

        /// <summary>
        /// 桌面歌词样式提交防抖：滑块/数字框连续变更（如拖动描边宽度滑块逐刻度触发 setter）
        /// 合并为一次全量设置落盘（SaveSettingAsync）+ 样式推送（RefreshStyle），
        /// 避免逐 tick 全量序列化写盘。与 ScheduleSettingsBroadcast 同款重启式定时器。
        /// </summary>
        private void ScheduleDesktopLyricsStyleCommit()
        {
            if (_desktopLyricsStyleCommitTimer is null)
            {
                _desktopLyricsStyleCommitTimer = App.MainWindow.DispatcherQueue.CreateTimer();
                _desktopLyricsStyleCommitTimer.Interval = TimeSpan.FromMilliseconds(300);
                _desktopLyricsStyleCommitTimer.Tick += (s, e) =>
                {
                    _desktopLyricsStyleCommitTimer?.Stop();
                    _ = _musicDatabaseService.SaveSettingAsync();
                    App.Services.GetRequiredService<DesktopLyricsViewModel>().RefreshStyleFromSettings();
                };
            }
            _desktopLyricsStyleCommitTimer.Start();
        }

        public float CustomOpacity
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.CustomAcrylicOpacity = value / 100;
                    if (IsInitialized)
                    {
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 50f;

        public bool IsUpdateBackDrop
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsUpdateBackDrop = value;
                    if (IsInitialized)
                    {
                        App.MainWindow?.UpdateBackdropActiveState(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment LyricsAlignment
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsSettings();
                    }
                }
            }
        } = Microsoft.Graphics.Canvas.Text.CanvasHorizontalAlignment.Left;

        public TextAlignment PlayingDetailAlignment
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                    OnPropertyChanged(nameof(EffectivePlayingDetailAlignment));
                }
            }
        } = TextAlignment.Left;

        public bool UsePlayingDetailAlignmentInPortrait
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                    OnPropertyChanged(nameof(EffectivePlayingDetailAlignment));
                }
            }
        } = false;

        public bool IsPortraitLayout
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(EffectivePlayingDetailAlignment));
                }
            }
        }

        public TextAlignment EffectivePlayingDetailAlignment =>
            IsPortraitLayout && !UsePlayingDetailAlignmentInPortrait
                ? TextAlignment.Left
                : PlayingDetailAlignment;

        public bool IsMusicInfoVisible
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = true;

        public bool IsGlobalFontSizeEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsGlobalFontSizeEnabled = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsFontSize();
                    }
                }
            }
        } = false;

        public double GlobalFontSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsFontSize();
                    }
                }
            }
        } = 32f;

        public double LyricsFontSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsFontSize();
                    }
                }
            }
        } = 32;

        public string MusicCoverCache
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.MusicCoverCache = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        public bool IsDopEnabled
        {
            get => field;
            set
            {
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

        public bool IsFadeEnabled
        {
            get => field;
            set
            {
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
        public ObservableCollection<int> DsdPcmFreqs
        {
            get => field;
            set => SetProperty(ref field, value);
        } = [44100, 88200, 176400, 352800];

        public int DsdPcmFreq
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        } = 88200;

        public float LyricsBlurAmount
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 5f;

        public double CharFloatAmount
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 5.0;

        public double CharScaleAmount
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 110.0;

        public double GlowAmount
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 5.0;

        public double LongSyllableThreshold
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 700.0;

        public double PlayingLineTopOffsetPercent
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 40.0;

        public double TranslatedOpacityPercent
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 60.0;

        public double UnplayedOpacityPercent
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = 50.0;

        public double TargetFrameRate
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        SendLyricsSettings();
                    }
                }
            }
        } = 60.0;

        public bool EnableAdvancedLyricsEffect
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public EasingType ScrollEasingType
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ScrollEasingTypeIndex));
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = EasingType.Sine;
        public int ScrollEasingTypeIndex
        {
            get => (int)ScrollEasingType;
            set => ScrollEasingType = (EasingType)value;
        }

        public EaseMode ScrollEasingMode
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    OnPropertyChanged(nameof(ScrollEasingModeIndex));
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        ScheduleSettingsBroadcast();
                    }
                }
            }
        } = EaseMode.Continuous;
        public int ScrollEasingModeIndex
        {
            get => (int)ScrollEasingMode;
            set => ScrollEasingMode = (EaseMode)value;
        }

        public bool EnableGlobalHotKey
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.EnableGlobalHotKey = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                        InitHotKeys();
                    }
                }
            }
        } = false;

        public bool IsTrimOnHideEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsTrimOnHideEnabled = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

        public bool IsTrimAfterPlaybackEnabled
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.IsTrimAfterPlaybackEnabled = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = false;

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
                                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayButton_Click();
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
                                App.Services.GetRequiredService<MusicBrowseViewModel>().NextMusicButton_Click();
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
                                App.Services.GetRequiredService<MusicBrowseViewModel>().LastMusicButton_Click();
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

        public void InitHotKeys()
        {
            var window = App.MainWindow;
            if (window is null) return;

            GlobalHotKeyHook.ConflictsChanged -= OnGlobalHotKeyConflictsChanged;
            GlobalHotKeyHook.ConflictsChanged += OnGlobalHotKeyConflictsChanged;
            GlobalHotKeyHook.ClearAll(window);

            if (!EnableGlobalHotKey) return;

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PlayOrPauseSong, PlayOrPauseShortcut, () =>
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().PlayButton_Click();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.NextSong, NextSongShortcut, () =>
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().NextMusicButton_Click();
            });

            GlobalHotKeyHook.UpdateHotKey(window, ShortcutId.PreviousSong, PreviousSongShortcut, () =>
            {
                App.Services.GetRequiredService<MusicBrowseViewModel>().LastMusicButton_Click();
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
                    if (!BassOutputDevices.AsValueEnumerable().Any(d => d.Name == name))
                    {
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiSharedText")}]",
                            Id = id,
                            OutputMode = "WasapiShared"
                        });
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                            Id = id,
                            OutputMode = "WasapiExclusivePush"
                        });
                        BassOutputDevices.Add(new BassOutputDevice
                        {
                            Name = name,
                            Tag = $"{name} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                            Id = id,
                            OutputMode = "WasapiExclusiveEvent"
                        });
                    }
                }

                var device = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == AppSettings.DeviceName && d.OutputMode == AppSettings.OutputMode);
                if (device is null)
                {
                    // 枚举不到已保存设备（未上电/驱动未就绪等瞬时原因）时只回退内存状态到默认设备，
                    // 不触发落盘，避免把用户保存的输出设备设置永久重置（下次启动设备在位时自动恢复）
                    AppSettings.OutputMode = "DirectSound";
                    AppSettings.BassOutputDeviceId = -1;
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
                }
            }
            finally { _isLoadingDevices = false; }
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
