using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;
using WinUIMusicPlayer.Utils;

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
            set {
                if (SetProperty(ref _dsdGain, value))
                {
                    // 更新应用设置
                    AppSettings.dsdGain = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }
        private bool _isAutoLyricsEnabled = true;
        public bool IsAutoLyricsEnabled
        {
            get => _isAutoLyricsEnabled;
            set {
                if (SetProperty(ref _isAutoLyricsEnabled, value))
                {
                    // 更新应用设置
                    AppSettings.isAutoLyricsEnabled = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }

        private bool _isRunningBackend = true;
        public bool IsRunningBackend
        {
            get => _isRunningBackend;
            set => SetProperty(ref _isRunningBackend, value);
        }
        private int _latency = 300;
        public int Latency
        {
            get => _latency;
            set {
                if (SetProperty(ref _latency, value))
                {
                    // 更新应用设置
                    AppSettings.Latency = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }
        private int _maxCoverPreLoadNum = 0;
        public int MaxCoverPreLoadNum
        {
            get => _maxCoverPreLoadNum;
            set {
                if (SetProperty(ref _maxCoverPreLoadNum, value))
                {
                    // 更新应用设置
                    AppSettings.maxCoverPreLoadNum = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }
        private bool _isCoverCacheEnabled = false;
        public bool IsCoverCacheEnabled
        {
            get => _isCoverCacheEnabled;
            set {
                if (SetProperty(ref _isCoverCacheEnabled, value))
                {
                    // 更新应用设置
                    AppSettings.isCoverCacheEnabled = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
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
            set {
                if (SetProperty(ref _defaultPlayListComboBoxTag, value))
                {
                    // 更新应用设置
                    AppSettings.DefualtPlayList = value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
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
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            }
        }

        private string _lrcAPISource = "LRC";
        public string LrcAPISource {
            get => _lrcAPISource;
            set {
                if (SetProperty(ref _lrcAPISource, value))
                {
                    // 更新应用设置
                    AppSettings.LrcAPISource = string.IsNullOrEmpty(value) ? "https://api.lrc.cx" : value;
                    // 保存设置
                    _ = MusicDatabaseService.SaveSettingAsync();
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
                    _ = MusicDatabaseService.SaveSettingAsync();
                    if (_isInitized) {
                        AppSettings.OnOutputSettingsChanged();
                    }                    
                }
            }
        }

        private ObservableCollection<string> _outputDevices = new ObservableCollection<string>();
        public ObservableCollection<string> OutputDevices
        {
            get => _outputDevices;
            set {
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
                    if (value != null) {
                        Debug.WriteLine($"DeviceName changed to: {value}");
                        // 更新应用设置
                        AppSettings.DeviceName = value;
                        // 保存设置
                        _ = MusicDatabaseService.SaveSettingAsync();
                        if (IsRealDevceChange)
                        {
                            if (_isInitized) {
                                AppSettings.OnOutputSettingsChanged();
                            }
                        }
                        else {
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
                    Debug.WriteLine($"BackdropType changed to: {value}");
                    // 保存设置
                    AppSettings.AppStyle = value;
                    _ = MusicDatabaseService.SaveSettingAsync();
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
                    _ = MusicDatabaseService.SaveSettingAsync();
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
            MaxCoverPreLoadNum = AppSettings.maxCoverPreLoadNum;
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
            _isInitized = true;
        }
        [RelayCommand]
        private void OnBackdropTypeChanged(string type)
        {
            try {
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
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
                    mainWindow.SetAppStyle();
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
            } catch (Exception ex) {
                Debug.WriteLine($"Error setting backdrop type: {ex.Message}");
            }
        }
        [RelayCommand]
        private void OnThemeTypeChanged(string type)
        {
            try
            {
                var mainWindow = (App.MainWindow as MainWindow);
                if (mainWindow != null)
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
                    mainWindow.SetAppTheme();
                    _ = MusicDatabaseService.SaveSettingAsync();
                }
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
            _ = MusicDatabaseService.SaveSettingAsync();
        }

        private void OnCoverSizeChanged(int value)
        {
            // 更新应用设置
            AppSettings.CoverSize = value;
            // 保存设置
            _ = MusicDatabaseService.SaveSettingAsync();
        }

        private void OnDefaultEntryComboBoxTagChanged(string value)
        {
            // 更新应用设置
            AppSettings.DefualtEntry = value;
            // 保存设置
            _ = MusicDatabaseService.SaveSettingAsync();
        }

        //public async Task SaveSetting()
        //{
        //    SaveSettings settings = await MusicDatabaseService.GetSettings();
        //    SaveSettings newSettings = new SaveSettings();
        //    newSettings.OutputMode = AppSettings.OutputMode;
        //    newSettings.Latency = AppSettings.Latency;
        //    newSettings.DeviceFriendlyName = AppSettings.DeviceName;
        //    newSettings.DefualtEntry = AppSettings.DefualtEntry;
        //    newSettings.DefualtPlayList = AppSettings.DefualtPlayList;
        //    newSettings.LrcAPISource = AppSettings.LrcAPISource;
        //    newSettings.LrcAPIAuth = AppSettings.LrcAPIAuth;
        //    newSettings.AppStyle = AppSettings.AppStyle;
        //    newSettings.AppTheme = AppSettings.AppTheme;
        //    newSettings.isCoverCacheEnabled = AppSettings.isCoverCacheEnabled;
        //    newSettings.maxCoverPreLoadNum = AppSettings.maxCoverPreLoadNum;
        //    newSettings.isRunningBackend = AppSettings.isRunningBackend;
        //    newSettings.isAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
        //    newSettings.dsdGain = AppSettings.dsdGain;
        //    newSettings.equalizerStr = ToolUtils.ConvertToJson(AppSettings.equalizer);
        //    newSettings.IsEqualizerEnabled = AppSettings.IsEqualizerEnabled;
        //    newSettings.EqualizerPreset = AppSettings.EqualizerPreset;
        //    newSettings.CoverSize = AppSettings.CoverSize;
        //    if (settings == null)
        //    {
        //        await MusicDatabaseService.InsertSettings(newSettings);
        //    }
        //    else
        //    {
        //        await MusicDatabaseService.UpdateSettings(newSettings);
        //    }
        //}
    }
}
