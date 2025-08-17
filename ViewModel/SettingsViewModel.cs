using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Windows.ApplicationModel.Core;
using Windows.Storage;
using Windows.System;
using WinUIMusicPlayer.Helper;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;
using WinUIMusicPlayer.View;

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
        private float _dsdGain = 6f;
        public float DsdGain
        {
            get => _dsdGain;
            set
            {
                if (SetProperty(ref _dsdGain, value))
                {
                    // 更新应用设置
                    AppSettings.dsdGain = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.isAutoLyricsEnabled = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.Latency = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    AppSettings.IsCustomAppSize = value;
                    if (_isInitized)
                    {
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
                    AppSettings.AppWidth = value;
                    if (_isInitized)
                    {
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
                    AppSettings.AppHeight = value;
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.isCoverCacheEnabled = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.DefualtPlayList = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.LrcAPIAuth = value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.LrcAPISource = string.IsNullOrEmpty(value) ? "https://api.lrc.cx" : value;
                    // 保存设置
                    if (_isInitized)
                    {
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
                    // 更新应用设置
                    AppSettings.OutputMode = value;
                    // 保存设置                   
                    if (_isInitized)
                    {
                        _ = MusicDatabaseService.SaveSettingAsync();
                        AppSettings.OnOutputSettingsChanged();
                    }
                }
            }
        }

        private ObservableCollection<string> _outputDevices = new ObservableCollection<string>();
        public ObservableCollection<string> OutputDevices
        {
            get => _outputDevices;
            set
            {
                if (SetProperty(ref _outputDevices, value))
                {
                    Debug.WriteLine("OutputDevices collection changed.");
                }
            }
        }
        private string _deviceName = "Default";
        public string DeviceName
        {
            get => _deviceName;
            set
            {
                if (SetProperty(ref _deviceName, value))
                {
                    if (value != null)
                    {
                        Debug.WriteLine($"DeviceName changed to: {value}");
                        // 更新应用设置
                        AppSettings.DeviceName = value;
                        // 保存设置                        
                        if (IsRealDevceChange)
                        {
                            if (_isInitized)
                            {
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
        private string _backdropType = "TransparentAcrylic";

        public string BackdropType
        {
            get => _backdropType;
            set
            {
                if (SetProperty(ref _backdropType, value))
                {
                    AppSettings.AppStyle = value;
                    if (_isInitized)
                    {
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
                    Debug.WriteLine($"ThemeType changed to: {value}");
                    // 保存设置
                    AppSettings.AppTheme = value;
                    if (_isInitized)
                    {
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
                    AppSettings.EntranceAnimationTime = value;
                    if (_isInitized)
                    {
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
                    AppSettings.SlideAnimationTime = value;
                    if (_isInitized)
                    {
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
                    AppSettings.DrillInAnimationTime = value;
                    if (_isInitized)
                    {
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
                    AppSettings.IsBackgroundCoverEnabled = value;
                    if (_isInitized)
                    {
                        _ = MusicDatabaseService.SaveSettingAsync();
                        App.Services.GetRequiredService<MusicBrowsePage>().ChangeAcrylicBrushBackgroundOpacity();
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
                    AppSettings.CoverLoadThreadCount = value;
                    if (_isInitized)
                    {
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
                    AppSettings.IsFolderWatchEnabled = value;
                    if (_isInitized)
                    {
                        _ = MusicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        }
        private ObservableCollection<string> _fontFamilyList;
        public ObservableCollection<string> FontFamilyList {
            get => _fontFamilyList;
            set => SetProperty(ref _fontFamilyList, value);
        }
        private string _fontFamily = string.Empty;
        public string FontFamily
        {
            get => _fontFamily;
            set
            {
                if (SetProperty(ref _fontFamily, value))
                {                    
                    if (_isInitized)
                    {
                        AppSettings.GlobalFont = new FontFamily(value);
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
            OutputDevices = new ObservableCollection<string>(AppSettings.outputDeviceList);
            // 初始化设备名称
            DeviceName = AppSettings.DeviceName;
            // 初始化背景类型
            BackdropType = AppSettings.AppStyle;
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
            FontFamilyList = new ObservableCollection<string>(AppSettings.FontFamilyList);
            Debug.WriteLine(AppSettings.GlobalFont.Source);
            FontFamily = ToolUtils.GetCleanFontName(AppSettings.GlobalFont.Source);
            _isInitized = true;
        }
        
        [RelayCommand]
        private void OnBackdropTypeChanged(string type)
        {
            try
            {
                switch (type)
                {
                    case "Acrylic":
                        // 设置为Acrylic背景
                        AppSettings.AppStyle = "Acrylic";
                        break;
                    case "TransparentAcrylic":
                        AppSettings.AppStyle = "TransparentAcrylic";
                        break;
                    case "Mica":
                        // 设置为Mica背景
                        AppSettings.AppStyle = "Mica";
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
    }
}
