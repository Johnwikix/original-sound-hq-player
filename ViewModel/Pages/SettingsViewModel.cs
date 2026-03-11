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
    public partial class SettingsViewModel : ObservableObject
    {       
        
        public AppViewModel AppViewModel { get; }

        public SettingsViewModel(AppViewModel appViewModel, MusicDatabaseService musicDatabaseService)
        {
            AppViewModel = appViewModel;
            InitializeData();            
        }

        private void InitializeData()
        {
            InitializeWasapiDevice();
        }

        public void InitializeWasapiDevice()
        {
            AppViewModel.BassOutputDevices.Clear();
            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = ToolUtils.GetString("DefaultDevice") + " [DirectSound]",
                Id = -1,
                OutputMode = "DirectSound"
            });
            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiSharedText")}]",
                Id = -1,
                OutputMode = "WasapiShared"
            });
            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
            {
                Name = "DefaultDevice",
                Tag = $"{ToolUtils.GetString("DefaultDevice")} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                Id = -1,
                OutputMode = "WasapiExclusivePush"
            });
            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
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
                        if (!AppViewModel.BassOutputDevices.AsValueEnumerable().Any(d => d.Name == deviceInfo.Name))
                        {
                            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Tag = $"{deviceInfo.Name} [{ToolUtils.GetString("WasapiSharedText")}]",
                                Id = i,
                                OutputMode = "WasapiShared"
                            });
                            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
                            {
                                Name = deviceInfo.Name,
                                Tag = $"{deviceInfo.Name} [{ToolUtils.GetString("WasapiExclusivePushText")}]",
                                Id = i,
                                OutputMode = "WasapiExclusivePush"
                            });
                            AppViewModel.BassOutputDevices.Add(new BassOutputDevice
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
            var device = AppViewModel.BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == AppSettings.DeviceName && d.OutputMode == AppSettings.OutputMode);
            if (device is null)
            {
                AppViewModel.SelectedDevice = AppViewModel.BassOutputDevices.AsValueEnumerable().FirstOrDefault(d => d.Name == "DefaultDevice" && d.OutputMode == "DirectSound");
                AppSettings.BassOutputDeviceId = -1;
            }
            else
            {
                AppViewModel.SelectedDevice = device;
            }
        }

        public void InitializeAsioDevice()
        {
            int n = BassAsio.DeviceCount;
            for (int i = 0; i < n; i++)
            {
                if (BassAsio.GetDeviceInfo(i, out AsioDeviceInfo deviceInfo))
                {
                    AppViewModel.BassOutputDevices.Add(new BassOutputDevice
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

        
    }
}
