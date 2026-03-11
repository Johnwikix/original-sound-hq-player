using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        public bool IsRealDevceChange { get; set; } = true;
        private int _coverSize = 0;
        public int CoverSize
        {
            get => _coverSize;
            set
            {
                if (SetProperty(ref _coverSize, value))
                {
                    AppSettings.CoverSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _dsdGain = 6;
        public int DsdGain
        {
            get => _dsdGain;
            set
            {
                if (SetProperty(ref _dsdGain, value))
                {
                    AppSettings.dsdGain = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }
        private bool _isAutoLyricsEnabled = true;
        public bool IsAutoLyricsEnabled
        {
            get => _isAutoLyricsEnabled;
            set
            {
                if (SetProperty(ref _isAutoLyricsEnabled, value))
                {
                    AppSettings.isAutoLyricsEnabled = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isRunningBackend = true;
        public bool IsRunningBackend
        {
            get => _isRunningBackend;
            set { 
                if(SetProperty(ref _isRunningBackend, value))
                {
                    AppSettings.isRunningBackend = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private int _latency = 300;
        public int Latency
        {
            get => _latency;
            set
            {
                if (SetProperty(ref _latency, value))
                {
                    AppSettings.Latency = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isCustomAppSize = false;
        public bool IsCustomAppSize
        {
            get => _isCustomAppSize;
            set
            {
                if (SetProperty(ref _isCustomAppSize, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsCustomAppSize = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _appWidth = 1440;
        public int AppWidth
        {
            get => _appWidth;
            set
            {
                if (SetProperty(ref _appWidth, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.AppWidth = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _appHeight = 810;
        public int AppHeight
        {
            get => _appHeight;
            set
            {
                if (SetProperty(ref _appHeight, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.AppHeight = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _defaultEntryComboBoxTag = "AddFolder";
        public string DefaultEntryComboBoxTag
        {
            get => _defaultEntryComboBoxTag;
            set
            {
                if (SetProperty(ref _defaultEntryComboBoxTag, value))
                {
                    // 值变更时的逻辑
                    OnDefaultEntryComboBoxTagChanged(value);
                }
            }
        }

        private string _defaultPlayListComboBoxTag = "song";
        public string DefaultPlayListComboBoxTag
        {
            get => _defaultPlayListComboBoxTag;
            set
            {
                if (SetProperty(ref _defaultPlayListComboBoxTag, value))
                {
                    if (IsInitialized)
                    {                       
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private ObservableCollection<BassOutputDevice> _bassOutputDevices = new();
        public ObservableCollection<BassOutputDevice> BassOutputDevices
        {
            get => _bassOutputDevices;
            set => SetProperty(ref _bassOutputDevices, value);
        }
        private BassOutputDevice _selectedDevice;
        public BassOutputDevice SelectedDevice
        {
            get => _selectedDevice;
            set
            {
                if (SetProperty(ref _selectedDevice, value))
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

        private string _backdropType = "TransparentAcrylic";

        public string BackdropType
        {
            get => _backdropType;
            set
            {
                if (SetProperty(ref _backdropType, value))
                {
                    AppSettings.AppStyle = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _themeType = "Dark"; // 默认值

        public string ThemeType
        {
            get => _themeType;
            set
            {
                if (SetProperty(ref _themeType, value))
                {
                    // 保存设置
                    AppSettings.AppTheme = value;
                    if (IsInitialized)
                    {                        
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private int _entranceAnimationTime;
        public int EntranceAnimationTime
        {
            get => _entranceAnimationTime;
            set
            {
                if (SetProperty(ref _entranceAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _slideAnimationTime;
        public int SlideAnimationTime
        {
            get => _slideAnimationTime;
            set
            {
                if (SetProperty(ref _slideAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _drillInAnimationTime;
        public int DrillInAnimationTime
        {
            get => _drillInAnimationTime;
            set
            {
                if (SetProperty(ref _drillInAnimationTime, value))
                {
                    if (IsInitialized)
                    {
                        //AppSettings.DrillInAnimationTime = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _version = string.Empty;
        public string Version
        {
            get => _version;
            set => SetProperty(ref _version, value);
        }

        private bool _isFolderWatchEnabled = true;
        public bool IsFolderWatchEnabled
        {
            get => _isFolderWatchEnabled;
            set
            {
                if (SetProperty(ref _isFolderWatchEnabled, value))
                {
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private ObservableCollection<FontInfo> _fontFamilyList;
        public ObservableCollection<FontInfo> FontFamilyList
        {
            get => _fontFamilyList;
            set => SetProperty(ref _fontFamilyList, value);
        }
        private FontInfo _fontFamily;
        public FontInfo FontFamily
        {
            get => _fontFamily;
            set
            {
                if (SetProperty(ref _fontFamily, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.GlobalFont = value.FontFamily;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isColorPickerVisible = false;
        public bool IsColorPickerVisible
        {
            get => _isColorPickerVisible;
            set => SetProperty(ref _isColorPickerVisible, value);
        }

        private Color _customColor = Color.FromArgb(255, 128, 128, 128);
        public Color CustomColor
        {
            get => _customColor;
            set
            {
                if (SetProperty(ref _customColor, value))
                {
                    AppSettings.CustomColorAlpha = value.A;
                    AppSettings.CustomColorRed = value.R;
                    AppSettings.CustomColorGreen = value.G;
                    AppSettings.CustomColorBlue = value.B;
                    if (IsInitialized)
                    {                        
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private float _customOpacity = 50f;
        public float CustomOpacity
        {
            get => _customOpacity;
            set
            {
                if (SetProperty(ref _customOpacity, value))
                {
                    AppSettings.CustomAcrylicOpacity = value / 100;
                    if (IsInitialized)
                    {                       
                        App.MainWindow?.SetCustomAppStyle();
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isUpdateBackDrop = false;
        public bool IsUpdateBackDrop
        {
            get => _isUpdateBackDrop;
            set
            {
                if (SetProperty(ref _isUpdateBackDrop, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsUpdateBackDrop = value;
                        App.MainWindow?.UpdateBackdropActiveState(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private string _lyricsAlignment = "Left";
        public string LyricsAlignment
        {
            get => _lyricsAlignment;
            set
            {
                if (SetProperty(ref _lyricsAlignment, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.LyricsAlignment = ToolUtils.ConvertStringToTextAlignment(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isGlobalFontSizeEnabled = false;
        public bool IsGlobalFontSizeEnabled
        {
            get => _isGlobalFontSizeEnabled;
            set
            {
                if (SetProperty(ref _isGlobalFontSizeEnabled, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsGlobalFontSizeEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private double _globalFontSize = 32f;
        public double GlobalFontSize
        {
            get => _globalFontSize;
            set
            {
                if (SetProperty(ref _globalFontSize, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.GlobalFontSize = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _musicCoverCache;
        public string MusicCoverCache
        {
            get => _musicCoverCache;
            set => SetProperty(ref _musicCoverCache, value);
        }

        private bool _isDopEnabled;
        public bool IsDopEnabled
        {
            get => _isDopEnabled;
            set
            {
                if (SetProperty(ref _isDopEnabled, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsDopEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private bool _isFadeEnabled;
        public bool IsFadeEnabled
        {
            get => _isFadeEnabled;
            set
            {
                if (SetProperty(ref _isFadeEnabled, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsFadeEnabled = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private string _dsdPcmFreq = "88200";
        public string DsdPcmFreq
        {
            get => _dsdPcmFreq;
            set
            {
                if (SetProperty(ref _dsdPcmFreq, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.dsdPcmFreq = int.Parse(value);
                        _ = _musicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsUpdated();
                    }
                }
            }
        }

        private bool _isWFWLyrics;
        public bool IsWFWLyrics
        {
            get => _isWFWLyrics;
            set
            {
                if (SetProperty(ref _isWFWLyrics, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.IsWFWLyrics = value;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private double _lyricsBlurAmount;
        public double LyricsBlurAmount
        {
            get => _lyricsBlurAmount;
            set
            {
                if (SetProperty(ref _lyricsBlurAmount, value))
                {
                    if (IsInitialized)
                    {
                        AppSettings.LyricsBlurAmount = value / 10;
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
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
                Debug.WriteLine($"Error setting backdrop type: {ex.Message}");
            }
        }
        [RelayCommand]
        private void OnThemeTypeChanged(string type)
        {
            try
            {
                switch (type)
                {
                    case "Default":
                        ThemeType = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                    case "Dark":
                        ThemeType = "Dark";
                        AppSettings.elementTheme = ElementTheme.Dark;
                        break;
                    case "Light":
                        ThemeType = "Light";
                        AppSettings.elementTheme = ElementTheme.Light;
                        break;
                    default:
                        ThemeType = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                }
                App.MainWindow?.SetAppTheme();
                if (IsInitialized)
                {
                    App.Services.GetRequiredService<PlayingDetailPage>().ChangeAcrylicBrushBackground();
                    App.Services.GetRequiredService<MusicBrowsePage>().ThemeChangedUpdateCover();
                    _ = _musicDatabaseService.SaveSettingAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting theme type: {ex.Message}");
            }
        }

        private void OnDefaultEntryComboBoxTagChanged(string value)
        {
            if (IsInitialized)
            {
                _ = _musicDatabaseService.SaveSettingAsync();
            }
        }

        [RelayCommand]
        private async void OpenLogPath()
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
        private async Task ChangeCoverCacheLocation()
        {
            var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
            PickFolderResult folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                MusicCoverCache = folder.Path;
                AppSettings.MusicCoverCache = folder.Path;
                if (IsInitialized)
                {
                    _ = _musicDatabaseService.SaveSettingAsync();
                }
            }
        }

        [RelayCommand]
        private void OpenWebSite()
        {
            _ = Launcher.LaunchUriAsync(new Uri("https://johnwikix.github.io/original-sound-player-page"));
        }

        [RelayCommand]
        private void OpenPlayerGitHub()
        {
            _ = Launcher.LaunchUriAsync(new Uri("https://github.com/Johnwikix/BassPlayerSharp"));
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
    }
}
