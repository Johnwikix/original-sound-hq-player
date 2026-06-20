using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;

using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Reader
{
    public class UsbStorageDeviceReader
    {
        private static ILogger<UsbStorageDeviceReader> _logger = App.GetLogger<UsbStorageDeviceReader>();

        public static async Task<List<UsbStorageDevice>> GetUsbStorageDevicesAsync()
        {
            var usbDevices = new List<UsbStorageDevice>();
            var processedPaths = new HashSet<string>();
            try
            {
                string aqsFilter = "System.Devices.InterfaceClassGuid:=\"{A5DCBF10-6530-11D2-901F-00C04FB951ED}\"";
                var deviceInformationCollection = await DeviceInformation.FindAllAsync(aqsFilter);

                foreach (var deviceInfo in deviceInformationCollection)
                {
                    foreach (var drive in DriveInfo.GetDrives())
                    {
                        if (drive.DriveType == DriveType.Removable && drive.IsReady)
                        {
                            if (!processedPaths.Contains(drive.Name))
                            {
                                string deviceId = await GetDeviceUniqueIdAsync(drive.Name);
                                var deviceInfoObj = new UsbStorageDevice
                                {
                                    Path = drive.Name,
                                    Name = drive.VolumeLabel,
                                    FreeSpaceInGB = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024)),
                                    TotalSpaceInGB = Math.Round((double)drive.TotalSize / (1024 * 1024 * 1024)),
                                    UniqueId = deviceId
                                };
                                usbDevices.Add(deviceInfoObj);
                                processedPaths.Add(drive.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetUsbStorageDevicesAsync 获取USB存储设备失败: {ex.Message}");
            }
            return usbDevices;
        }

        private static async Task<string> GetDeviceUniqueIdAsync(string drivePath)
        {
            try
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(drivePath);
                IDictionary<string, object> properties = await folder.Properties.RetrievePropertiesAsync(
                    new List<string> { "System.Devices.DeviceInstanceId" });

                if (properties.TryGetValue("System.Devices.DeviceInstanceId", out object instanceIdObj) &&
                    instanceIdObj is string deviceInstanceId &&
                    !string.IsNullOrEmpty(deviceInstanceId))
                {
                    string serialNumber = deviceInstanceId.Contains("\\")
                        ? deviceInstanceId.Split('\\').AsValueEnumerable().Last()
                        : string.Empty;

                    if (!string.IsNullOrEmpty(serialNumber))
                    {
                        return serialNumber;
                    }

                    if (deviceInstanceId.Contains("VID_") && deviceInstanceId.Contains("PID_"))
                    {
                        int vidIndex = deviceInstanceId.IndexOf("VID_");
                        int pidIndex = deviceInstanceId.IndexOf("PID_");
                        if (vidIndex >= 0 && pidIndex >= 0)
                        {
                            string vendorId = deviceInstanceId.Substring(vidIndex + 4, 4);
                            string productId = deviceInstanceId.Substring(pidIndex + 4, 4);
                            return $"VID_{vendorId}_PID_{productId}";
                        }
                    }

                    return deviceInstanceId;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetDeviceUniqueIdAsync Windows.Storage API获取设备ID失败: {ex.Message}");
            }

            try
            {
                DriveInfo drive = new DriveInfo(drivePath);
                return $"{drive.Name}_{drive.VolumeLabel}_{drive.TotalSize}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetDeviceUniqueIdAsync 备用方法获取设备ID失败: {ex.Message}");
                return Guid.NewGuid().ToString();
            }
        }
    }
}
