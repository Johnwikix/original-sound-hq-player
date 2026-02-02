using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ManagedBass.Asio;
using ManagedBass.Wasapi;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.Windows.Storage.Pickers;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Windows.Storage;
using Windows.System;
using Windows.UI;
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

        //private string _lrcAPIAuth = string.Empty;
        //public string LrcAPIAuth
        //{
        //    get => _lrcAPIAuth;
        //    set
        //    {
        //        if (SetProperty(ref _lrcAPIAuth, value))
        //        {
        //            if (_isInitized)
        //            {
        //                AppSettings.LrcAPIAuth = value;
        //                _ = MusicDatabaseService.SaveSettingAsync();
        //            }
        //        }
        //    }
        //}

        //private string _lrcAPISource = "LRC";
        //public string LrcAPISource
        //{
        //    get => _lrcAPISource;
        //    set
        //    {
        //        if (SetProperty(ref _lrcAPISource, value))
        //        {
        //            if (_isInitized)
        //            {
        //                AppSettings.LrcAPISource = value;
        //                _ = MusicDatabaseService.SaveSettingAsync();
        //            }
        //        }
        //    }
        //}


        public AppObservableObj AppObservableObj { get; }
        private MusicDatabaseService MusicDatabaseService { get; }

        public SettingsViewModel(AppObservableObj appObservableObj,MusicDatabaseService musicDatabaseService)
        {
            AppObservableObj = appObservableObj;
            MusicDatabaseService = musicDatabaseService;
            InitializeData();            
        }

        private void InitializeData()
        {
            _isInitized = false;
            //CoverSize = AppSettings.CoverSize;
            //DsdGain = AppSettings.dsdGain;
            //IsAutoLyricsEnabled = AppSettings.isAutoLyricsEnabled;
            //IsRunningBackend = AppSettings.isRunningBackend;  
            //Latency = AppSettings.Latency;
            //IsCoverCacheEnabled = AppSettings.isCoverCacheEnabled;
            //DefaultEntryComboBoxTag = AppSettings.DefualtEntry; 
            //DefaultPlayListComboBoxTag = AppSettings.DefualtPlayList;
            //LrcAPIAuth = AppSettings.LrcAPIAuth;
            //LrcAPISource = AppSettings.LrcAPISource;
            //BackdropType = AppSettings.AppStyle;
            //if (BackdropType != "CustomAcrylicStyle")
            //{
            //    IsColorPickerVisible = false;
            //}
            //else
            //{
            //    IsColorPickerVisible = true;
            //}
            //CustomOpacity = AppSettings.CustomAcrylicOpacity * 100;
            //CustomColor = Color.FromArgb(AppSettings.CustomColorAlpha,
            //                                     AppSettings.CustomColorRed,
            //                                     AppSettings.CustomColorGreen,
            //                                     AppSettings.CustomColorBlue);
            //ThemeType = AppSettings.AppTheme;
            //EntranceAnimationTime = AppSettings.EntranceAnimationTime;
            //SlideAnimationTime = AppSettings.SlideAnimationTime;
            //DrillInAnimationTime = AppSettings.DrillInAnimationTime;
            //IsBackgroundCoverEnabled = AppSettings.IsBackgroundCoverEnabled;
            //IsFolderWatchEnabled = AppSettings.IsFolderWatchEnabled;
            //IsCustomAppSize = AppSettings.IsCustomAppSize;
            //AppHeight = AppSettings.AppHeight;
            //AppWidth = AppSettings.AppWidth;
            //Version = $"{Windows.ApplicationModel.Package.Current.Id.Version.Major}.{Windows.ApplicationModel.Package.Current.Id.Version.Minor}.{Windows.ApplicationModel.Package.Current.Id.Version.Build}.{Windows.ApplicationModel.Package.Current.Id.Version.Revision}";
            //FontFamilyList = new ObservableCollection<FontInfo>(AppSettings.FontFamilyList);
            //FontFamily = FontFamilyList.AsValueEnumerable().FirstOrDefault(f => f.Name == ToolUtils.GetCleanFontName(AppSettings.GlobalFont.Source));
            //IsDopEnabled = AppSettings.IsDopEnabled;
            //IsFadeEnabled = AppSettings.IsFadeEnabled;
            //IsUpdateBackDrop = AppSettings.IsUpdateBackDrop;
            //LyricsAlignment = ToolUtils.ConvertTextAlignmentToString(AppSettings.LyricsAlignment);
            //LyricsMargin = AppSettings.LyricsMargin;
            //IsGlobalFontSizeEnabled = AppSettings.IsGlobalFontSizeEnabled;
            //GlobalFontSize = AppSettings.GlobalFontSize;
            //MusicCoverCache = AppSettings.MusicCoverCache;
            //DsdPcmFreq = AppSettings.dsdPcmFreq.ToString();
            //IsWFWLyrics = AppSettings.IsWFWLyrics;
            //LyricsBlurAmount = AppSettings.LyricsBlurAmount * 10;
            InitializeWasapiDevice();
            _isInitized = true;
        }

        public void InitializeWasapiDevice()
        {
            BassOutputDevices.Clear();
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
            InitializeAsioDevice();
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
                            Debug.WriteLine($"Wasapi:{deviceInfo.Name}: {i}");
                            BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Tag = $"{deviceInfo.Name} [{ToolUtils.GetString("WasapiSharedText")}]",
                                Id = i,
                                OutputMode = "WasapiShared"
                            });
                            BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Tag = $"{deviceInfo.Name} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                                Id = i,
                                OutputMode = "WasapiExclusivePush"
                            });
                            BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Tag = $"{deviceInfo.Name} [{ToolUtils.GetString("WasapiExclusiveEventText")}]",
                                Id = i,
                                OutputMode = "WasapiExclusiveEvent"
                            });
                        }
                    }
                }
            }
            var deviceName = AppSettings.DeviceName;
            var outputMode = AppSettings.OutputMode;
            var device = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == AppSettings.DeviceName && d.OutputMode == AppSettings.OutputMode);
            if (device is null)
            {
                SelectedDevice = BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == "DefaultDevice" && d.OutputMode == "DirectSound");
                AppSettings.BassOutputDeviceId = -1;
            }
            else
            {
                SelectedDevice = device;
            }
        }

        public void InitializeAsioDevice()
        {
            int n = BassAsio.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                if (BassAsio.GetDeviceInfo(i, out AsioDeviceInfo deviceInfo))
                {
                    Debug.WriteLine($"Asio:{deviceInfo.Name}: {i}");
                    Debug.WriteLine($"AsioDriver:{deviceInfo.Driver}: {i}");
                    BassOutputDevices.Add(new BassOutputDevice
                    {
                        Name = deviceInfo.Name,
                        Tag = deviceInfo.Name + " [ASIO]",
                        AsioId = i,
                        OutputMode = "ASIO"
                    });
                }
            }
        }

        public Visibility CheckSystemVersion()
        {
            if (Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major == 10 && Environment.OSVersion.Version.Minor == 0)
            {
                // 获取内部版本号（Build）
                int buildNumber = Environment.OSVersion.Version.Build;
                if (buildNumber >= 22000)
                {
                    return Visibility.Visible;
                }
                else
                {
                    return Visibility.Collapsed;
                }
            }
            return Visibility.Collapsed;
        }

        [RelayCommand]
        private void OnBackdropTypeChanged(string type)
        {
            try
            {
                switch (type)
                {
                    case "Acrylic":
                        AppObservableObj.BackdropType = "Acrylic";
                        break;
                    case "TransparentAcrylic":
                        AppObservableObj.BackdropType = "TransparentAcrylic";
                        break;
                    case "Mica":
                        AppObservableObj.BackdropType = "Mica";
                        break;
                    case "TransparentTint":
                        AppObservableObj.BackdropType = "TransparentTint";
                        break;
                    case "CustomAcrylicStyle":
                        AppObservableObj.BackdropType = "CustomAcrylicStyle";
                        break;
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
                        AppObservableObj.ThemeType = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                    case "Dark":
                        AppObservableObj.ThemeType = "Dark";
                        AppSettings.elementTheme = ElementTheme.Dark;
                        break;
                    case "Light":
                        AppObservableObj.ThemeType = "Light";
                        AppSettings.elementTheme = ElementTheme.Light;
                        break;
                    default:
                        AppObservableObj.ThemeType = "Default";
                        AppSettings.elementTheme = ElementTheme.Default;
                        break;
                }
                App.MainWindow?.SetAppTheme();
                if (_isInitized)
                {
                    App.Services.GetRequiredService<MusicBrowsePage>().ChangeAcrylicBrushBackground();
                    App.Services.GetRequiredService<MusicBrowsePage>().ThemeChangedUpdateCover();
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
            if (_isInitized)
            {
                _ = MusicDatabaseService.SaveSettingAsync();
            }
        }

        //private void OnCoverSizeChanged(int value)
        //{
            
        //}

        //private void OnDefaultEntryComboBoxTagChanged(string value)
        //{
           
        //}

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
            var folderPicker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.MainWindow.AppWindow.Id);
            PickFolderResult folder = await folderPicker.PickSingleFolderAsync();
            if (folder is not null)
            {
                AppObservableObj.MusicCoverCache = folder.Path;
                //AppSettings.MusicCoverCache = folder.Path;
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
    }
}
