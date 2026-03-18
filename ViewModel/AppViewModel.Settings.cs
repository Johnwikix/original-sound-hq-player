using CommunityToolkit.Mvvm.Input;
using ManagedBass.Asio;
using ManagedBass.Wasapi;
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
using ZLinq;

namespace WinUIMusicPlayer.ViewModel
{
    public partial class AppViewModel
    {
        public bool IsRealDevceChange { get; set; } = true;
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
        public int CoverSize
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.CoverSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 0;

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
                            App.Services.GetRequiredService<MusicBrowsePage>().ThemeChangedUpdateCover();
                            _ = _musicDatabaseService.SaveSettingAsync();
                        }
                    }
                    catch
                    {
                    }
                }
            }
        } = "Default";

        public bool IsDarkMode { get => field; set => SetProperty(ref field, value);} = false;

        public int EntranceAnimationTime
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

        public int SlideAnimationTime
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
        } = 400;

        public int DrillInAnimationTime
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
        } = 400;

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
                    AppSettings.GlobalFont = value.FontFamily;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
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
        } = Color.FromArgb(255, 128, 128, 128);

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

        public string LyricsAlignment
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.LyricsAlignment = ToolUtils.ConvertStringToTextAlignment(value);
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = "Left";

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
                    AppSettings.GlobalFontSize = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
        } = 32f;

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

        public bool IsWFWLyrics
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
        }

        public double LyricsBlurAmount
        {
            get => field;
            set
            {
                if (SetProperty(ref field, value))
                {
                    AppSettings.LyricsBlurAmount = value;
                    if (IsInitialized)
                    {
                        _ = _musicDatabaseService.SaveSettingAsync();
                    }
                }
            }
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
