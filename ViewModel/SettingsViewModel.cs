using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.Storage;
using Windows.System;
using Windows.UI;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class SettingsViewModel : ObservableObject
    {
        private bool _isInitized = false;
        public bool IsRealDevceChange = true;
        private int _coverSize = 0;
        public int CoverSize
        {
            get => _coverSize;
            set
            {
                if (SetProperty(ref _coverSize, value))
                {
                    // 值变更时的逻辑
                    OnCoverSizeChanged(value);
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
                    if (_isInitized)
                    {
                        AppSettings.dsdGain = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.isAutoLyricsEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isRunningBackend = true;
        public bool IsRunningBackend
        {
            get => _isRunningBackend;
            set => SetProperty(ref _isRunningBackend, value);
        }

        private bool _isProcessAboveNormal = false;
        public bool IsProcessAboveNormal
        {
            get => _isProcessAboveNormal;
            set => SetProperty(ref _isProcessAboveNormal, value);
        }

        private int _latency = 300;
        public int Latency
        {
            get => _latency;
            set
            {
                if (SetProperty(ref _latency, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.Latency = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.IsCustomAppSize = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.AppWidth = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.AppHeight = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private bool _isCoverCacheEnabled = false;
        public bool IsCoverCacheEnabled
        {
            get => _isCoverCacheEnabled;
            set
            {
                if (SetProperty(ref _isCoverCacheEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.isCoverCacheEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isSongCoverEnabled;
        public bool IsSongCoverEnabled
        {
            get => _isSongCoverEnabled;
            set
            {
                if (SetProperty(ref _isSongCoverEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsSongCoverEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isFavouriteCoverEnabled = false;
        public bool IsFavouriteCoverEnabled
        {
            get => _isFavouriteCoverEnabled;
            set
            {
                if (SetProperty(ref _isFavouriteCoverEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsFavouriteCoverEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isPlayListCoverEnabled = false;
        public bool IsPlayListCoverEnabled
        {
            get => _isPlayListCoverEnabled;
            set
            {
                if (SetProperty(ref _isPlayListCoverEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsPlayListCoverEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isSongCollectionCoverEnabled = false;
        public bool IsSongCollectionCoverEnabled
        {
            get => _isSongCollectionCoverEnabled;
            set
            {
                if (SetProperty(ref _isSongCollectionCoverEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsSongCollectionCoverEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.DefualtPlayList = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private string _lrcAPIAuth = string.Empty;
        public string LrcAPIAuth
        {
            get => _lrcAPIAuth;
            set
            {
                if (SetProperty(ref _lrcAPIAuth, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.LrcAPIAuth = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _lrcAPISource = "LRC";
        public string LrcAPISource
        {
            get => _lrcAPISource;
            set
            {
                if (SetProperty(ref _lrcAPISource, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.LrcAPISource = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private string _outputModeTag = "WaveOut";
        public string OutputModeTag
        {
            get => _outputModeTag;
            set
            {
                if (SetProperty(ref _outputModeTag, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.OutputMode = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsChanged();
                    }
                }
            }
        }

        //private ObservableCollection<string> _outputDevices = new ObservableCollection<string>();
        //public ObservableCollection<string> OutputDevices
        //{
        //    get => _outputDevices;
        //    set
        //    {
        //        if (SetProperty(ref _outputDevices, value))
        //        {
        //            Debug.WriteLine("OutputDevices collection changed.");
        //        }
        //    }
        //}
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
                            if (_isInitized)
                            {
                                AppSettings.DeviceName = value.Name;
                                AppSettings.BassOutputDeviceId = value.Id;
                                _ = MusicDatabaseService.SaveSettingAsync();
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
        //private string _deviceName = "Default";
        //public string DeviceName
        //{
        //    get => _deviceName;
        //    set
        //    {
        //        if (SetProperty(ref _deviceName, value))
        //        {
        //            if (value is not null)
        //            {                       
        //                if (IsRealDevceChange)
        //                {
        //                    if (_isInitized)
        //                    {
        //                        AppSettings.DeviceName = value;
        //                        _ = MusicDatabaseService.SaveSettingAsync();
        //                        AppSettings.OnOutputSettingsChanged();
        //                    }
        //                }
        //                else
        //                {
        //                    IsRealDevceChange = true;
        //                }
        //            }
        //        }
        //    }
        //}
        private string _backdropType = "TransparentAcrylic";

        public string BackdropType
        {
            get => _backdropType;
            set
            {
                if (SetProperty(ref _backdropType, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.AppStyle = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.AppTheme = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.EntranceAnimationTime = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.SlideAnimationTime = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.DrillInAnimationTime = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        private bool _isIsBackgroundCoverEnabled = false;
        public bool IsBackgroundCoverEnabled
        {
            get => _isIsBackgroundCoverEnabled;
            set
            {
                if (SetProperty(ref _isIsBackgroundCoverEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsBackgroundCoverEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
                        App.Services.GetRequiredService<MusicBrowsePage>()?.ChangeAcrylicBrushBackgroundOpacity();
                    }
                }
            }
        }

        private int _coverLoadThreadCount = 8;
        public int CoverLoadThreadCount
        {
            get => _coverLoadThreadCount;
            set
            {
                if (SetProperty(ref _coverLoadThreadCount, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.CoverLoadThreadCount = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.IsFolderWatchEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.GlobalFont = value.FontFamily;
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }

        //private bool _isGlobalFFmpegEnabled = true;
        //public bool IsGlobalFFmpegEnabled
        //{
        //    get => _isGlobalFFmpegEnabled;
        //    set
        //    {
        //        if (SetProperty(ref _isGlobalFFmpegEnabled, value))
        //        {
        //            if (_isInitized)
        //            {
        //                AppSettings.IsGlobalFFmpegEnabled = value;
        //                _ = MusicDatabaseService.SaveSettingAsync();
        //            }
        //        }
        //    }
        //}

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
                    if (_isInitized)
                    {
                        AppSettings.CustomColorAlpha = value.A;
                        AppSettings.CustomColorRed = value.R;
                        AppSettings.CustomColorGreen = value.G;
                        AppSettings.CustomColorBlue = value.B;
                        App.MainWindow?.SetCustomAppStyle();
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.CustomAcrylicOpacity = value / 100;
                        App.MainWindow?.SetCustomAppStyle();
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.IsUpdateBackDrop = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.LyricsAlignment = ToolUtils.ConvertStringToTextAlignment(value);
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private int _lyricsMargin = 0;
        public int LyricsMargin
        {
            get => _lyricsMargin;
            set
            {
                if (SetProperty(ref _lyricsMargin, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.LyricsMargin = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.IsGlobalFontSizeEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.GlobalFontSize = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
        public bool IsDopEnabled { 
            get => _isDopEnabled;
            set {
                if (SetProperty(ref _isDopEnabled, value))
                {
                    if (_isInitized)
                    {
                        AppSettings.IsDopEnabled = value;
                        _ = MusicDatabaseService.SaveSettingAsync();
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
                    if (_isInitized)
                    {
                        AppSettings.dsdPcmFreq = int.Parse(value);
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }


        public SettingsViewModel()
        {
            InitializeData();
        }

        private void InitializeData()
        {
            _isInitized = false;
            // 初始化封面大小  
            CoverSize = AppSettings.CoverSize;
            // 初始化DSD增益  
            DsdGain = AppSettings.dsdGain;
            // 初始化自动歌词开关  
            IsAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
            // 初始化后台运行开关  
            IsRunningBackend = AppSettings.isRunningBackend;
            // 初始化延迟  
            Latency = AppSettings.Latency;
            // 初始化最大封面预加载数量  
            //MaxCoverPreLoadNum = AppSettings.maxCoverPreLoadNum;
            // 初始化封面缓存开关  
            IsCoverCacheEnabled = AppSettings.isCoverCacheEnabled;
            // 初始化默认入口  
            DefaultEntryComboBoxTag = AppSettings.DefualtEntry;
            // 初始化默认播放列表  
            DefaultPlayListComboBoxTag = AppSettings.DefualtPlayList;
            // 初始化LRC API认证信息  
            LrcAPIAuth = AppSettings.LrcAPIAuth;
            // 初始化LRC API源  
            LrcAPISource = AppSettings.LrcAPISource;
            // 初始化输出模式  
            OutputModeTag = AppSettings.OutputMode;
            // 初始化输出设备列表  
            //OutputDevices = new ObservableCollection<string>(AppSettings.outputDeviceList);
            // 初始化设备名称
            //DeviceName = AppSettings.DeviceName;
            // 初始化背景类型
            BackdropType = AppSettings.AppStyle;
            if (BackdropType != "CustomAcrylicStyle")
            {
                IsColorPickerVisible = false;
            }
            else
            {
                IsColorPickerVisible = true;
            }
            CustomOpacity = AppSettings.CustomAcrylicOpacity * 100;
            CustomColor = Color.FromArgb(AppSettings.CustomColorAlpha,
                                                 AppSettings.CustomColorRed,
                                                 AppSettings.CustomColorGreen,
                                                 AppSettings.CustomColorBlue);
            // 初始化主题类型
            ThemeType = AppSettings.AppTheme;
            // 初始化入口动画时间
            EntranceAnimationTime = AppSettings.EntranceAnimationTime;
            // 初始化滑动动画时间
            SlideAnimationTime = AppSettings.SlideAnimationTime;
            // 初始化钻入动画时间
            DrillInAnimationTime = AppSettings.DrillInAnimationTime;
            // 初始化进程优先级
            IsProcessAboveNormal = AppSettings.IsProcessAboveNormal;
            // 初始化背景封面开关
            IsBackgroundCoverEnabled = AppSettings.IsBackgroundCoverEnabled;
            // 初始化文件夹监视开关
            IsFolderWatchEnabled = AppSettings.IsFolderWatchEnabled;
            // 初始化封面加载线程数
            CoverLoadThreadCount = AppSettings.CoverLoadThreadCount;
            // 是否启用自定义窗口大小
            IsCustomAppSize = AppSettings.IsCustomAppSize;
            // 高度
            AppHeight = AppSettings.AppHeight;
            // 宽度
            AppWidth = AppSettings.AppWidth;
            // 版本
            Version = $"{Windows.ApplicationModel.Package.Current.Id.Version.Major}.{Windows.ApplicationModel.Package.Current.Id.Version.Minor}.{Windows.ApplicationModel.Package.Current.Id.Version.Build}.{Windows.ApplicationModel.Package.Current.Id.Version.Revision}";
            FontFamilyList = new ObservableCollection<FontInfo>(AppSettings.FontFamilyList);
            FontFamily = FontFamilyList.FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(AppSettings.GlobalFont.Source));
            //IsGlobalFFmpegEnabled = AppSettings.IsGlobalFFmpegEnabled;
            IsDopEnabled = AppSettings.IsDopEnabled;
            IsUpdateBackDrop = AppSettings.IsUpdateBackDrop;
            LyricsAlignment = ToolUtils.ConvertTextAlignmentToString(AppSettings.LyricsAlignment);
            LyricsMargin = AppSettings.LyricsMargin;
            IsGlobalFontSizeEnabled = AppSettings.IsGlobalFontSizeEnabled;
            GlobalFontSize = AppSettings.GlobalFontSize;
            IsSongCoverEnabled = AppSettings.IsSongCoverEnabled;
            IsSongCollectionCoverEnabled = AppSettings.IsSongCollectionCoverEnabled;
            IsFavouriteCoverEnabled = AppSettings.IsFavouriteCoverEnabled;
            IsPlayListCoverEnabled = AppSettings.IsPlayListCoverEnabled;
            MusicCoverCache = AppSettings.MusicCoverCache;
            DsdPcmFreq = AppSettings.dsdPcmFreq.ToString();
            InitializeWasapiDevice();
            _isInitized = true;
        }

        public void InitializeWasapiDevice()
        {
            BassOutputDevices.Clear();
            int n = BassWasapi.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                if (BassWasapi.GetDeviceInfo(i, out WasapiDeviceInfo deviceInfo))
                {
                    // 筛选有效的WASAPI设备：已启用且是WASAPI设备
                    if (deviceInfo.IsEnabled && deviceInfo.Type != WasapiDeviceType.Microphone)
                    {
                        if (!BassOutputDevices.AsValueEnumerable().Any(d => d.Name == deviceInfo.Name))
                        {
                            Debug.WriteLine($"{deviceInfo.Name}: {i}");
                            BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Id = i
                            });
                        }
                    }
                }
            }
            BassOutputDevices.Add(new BassOutputDevice
            {
                Name = ToolUtils.GetString("DefaultDevice"),
                Id = -1
            });
            var device= BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == AppSettings.DeviceName);
            if (device is null)
            {
                SelectedDevice = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == ToolUtils.GetString("DefaultDevice"));
            }
            else {
                SelectedDevice = device;
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
                        AppSettings.AppStyle = "Acrylic";
                        IsColorPickerVisible = false;
                        break;
                    case "TransparentAcrylic":
                        AppSettings.AppStyle = "TransparentAcrylic";
                        IsColorPickerVisible = false;
                        break;
                    case "Mica":
                        AppSettings.AppStyle = "Mica";
                        IsColorPickerVisible = false;
                        break;
                    case "TransparentTint":
                        AppSettings.AppStyle = "TransparentTint";
                        IsColorPickerVisible = false;
                        break;
                    case "CustomAcrylicStyle":
                        AppSettings.AppStyle = "CustomAcrylicStyle";
                        IsColorPickerVisible = true;
                        break;
                }
                App.MainWindow?.SetAppStyle();
                if (_isInitized)
                {
                    _ = MusicDatabaseService.SaveSettingAsync();
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
                        AppSettings.AppTheme = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                    case "Dark":
                        AppSettings.AppTheme = "Dark";
                        AppSettings.elementTheme = ElementTheme.Dark;
                        break;
                    case "Light":
                        AppSettings.AppTheme = "Light";
                        AppSettings.elementTheme = ElementTheme.Light;
                        break;
                    default:
                        AppSettings.AppTheme = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                }
                App.MainWindow?.SetAppTheme();
                if (_isInitized)
                {
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
                App.Services.GetRequiredService<MusicBrowsePage>().ChangeAcrylicBrushBackground();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error setting theme type: {ex.Message}");
            }
        }

        [RelayCommand]
        private void OnBackgroundRunningChanged(string parameter)
        {
            switch (parameter)
            {
                case "Closed":
                    AppSettings.isRunningBackend = false;
                    break;

                case "RunningBackend":
                    AppSettings.isRunningBackend = true;
                    break;
            }
            if (_isInitized)
            {
                _ = MusicDatabaseService.SaveSettingAsync();
            }
        }

        [RelayCommand]
        private void OnProcessPriorityChanged(string parameter)
        {
            switch (parameter)
            {
                case "Normal":
                    AppSettings.IsProcessAboveNormal = false;
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.Normal);
                    break;

                case "AboveNormal":
                    AppSettings.IsProcessAboveNormal = true;
                    PowerManagementHelper.SetProcessPriority(Helper.ProcessPriorityClass.AboveNormal);
                    break;
            }
            if (_isInitized)
            {
                _ = MusicDatabaseService.SaveSettingAsync();
            }
        }

        private void OnCoverSizeChanged(int value)
        {
            // 更新应用设置
            AppSettings.CoverSize = value;
            // 保存设置
            if (_isInitized)
            {
                _ = MusicDatabaseService.SaveSettingAsync();
            }
        }

        private void OnDefaultEntryComboBoxTagChanged(string value)
        {
            // 更新应用设置
            AppSettings.DefualtEntry = value;
            // 保存设置
            if (_isInitized)
            {
                _ = MusicDatabaseService.SaveSettingAsync();
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
        private async void ChangeCoverCacheLocation()
        {
            var folderPicker = new Windows.Storage.Pickers.FolderPicker();
            folderPicker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, AppData.m_hWnd);
            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                MusicCoverCache = folder.Path;
                AppSettings.MusicCoverCache = folder.Path;
                if (_isInitized)
                {
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }

        [RelayCommand]
        private void OpenWebSite()
        {
            _ = Launcher.LaunchUriAsync(new Uri("https://johnwikix.github.io/original-sound-player-page"));
        }
    }
}
