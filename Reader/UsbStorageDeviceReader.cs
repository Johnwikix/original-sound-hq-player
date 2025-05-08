using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Reader
{
    public class UsbStorageDeviceReader
    {
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
                Console.WriteLine($"在获取 USB 存储设备时发生错误: {ex.Message}");
            }
            return usbDevices;
        }

        private static async Task<string> GetDeviceUniqueIdAsync(string drivePath)
        {
            string uniqueId = string.Empty;
            try
            {
                // 方法1: 使用 WMI 获取设备的序列号和硬件ID
                string volumeLabel = drivePath.TrimEnd('\\');
                using (var searcher = new ManagementObjectSearcher(@"SELECT * FROM Win32_DiskDrive WHERE InterfaceType='USB'"))
                {
                    foreach (var diskDrive in searcher.Get())
                    {
                        string deviceId = diskDrive["PNPDeviceID"]?.ToString() ?? string.Empty;
                        string serialNumber = deviceId.Contains("\\") ? deviceId.Split('\\').Last() : string.Empty;

                        // 查找对应的分区和逻辑磁盘
                        using (var partitionSearcher = new ManagementObjectSearcher(
                            $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{diskDrive["DeviceID"]}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                        {
                            foreach (var partition in partitionSearcher.Get())
                            {
                                using (var logicalDiskSearcher = new ManagementObjectSearcher(
                                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partition["DeviceID"]}'}} WHERE AssocClass=Win32_LogicalDiskToPartition"))
                                {
                                    foreach (var logicalDisk in logicalDiskSearcher.Get())
                                    {
                                        string driveLetter = logicalDisk["DeviceID"].ToString();
                                        if (driveLetter.Equals(volumeLabel, StringComparison.OrdinalIgnoreCase))
                                        {
                                            // 找到匹配的驱动器，使用序列号作为唯一ID
                                            uniqueId = serialNumber;

                                            // 如果序列号为空，使用完整设备ID作为备用
                                            if (string.IsNullOrEmpty(uniqueId))
                                            {
                                                uniqueId = deviceId;
                                            }

                                            // 还可以添加更多标识信息，如制造商ID、产品ID等
                                            string vendorId = string.Empty;
                                            string productId = string.Empty;

                                            if (deviceId.Contains("VID_") && deviceId.Contains("PID_"))
                                            {
                                                int vidIndex = deviceId.IndexOf("VID_");
                                                int pidIndex = deviceId.IndexOf("PID_");

                                                if (vidIndex >= 0 && pidIndex >= 0)
                                                {
                                                    vendorId = deviceId.Substring(vidIndex + 4, 4);
                                                    productId = deviceId.Substring(pidIndex + 4, 4);

                                                    // 如果序列号为空，可以使用VID_PID作为替代
                                                    if (string.IsNullOrEmpty(uniqueId))
                                                    {
                                                        uniqueId = $"VID_{vendorId}_PID_{productId}";
                                                    }
                                                }
                                            }

                                            return uniqueId;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(uniqueId))
                {
                    try
                    {
                        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(drivePath);
                        IDictionary<string, object> properties = await folder.Properties.RetrievePropertiesAsync(
                            new List<string> { "System.Devices.DeviceInstanceId" });

                        if (properties.ContainsKey("System.Devices.DeviceInstanceId") &&
                            properties["System.Devices.DeviceInstanceId"] != null)
                        {
                            uniqueId = properties["System.Devices.DeviceInstanceId"].ToString();
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"使用Windows.Storage API获取设备ID时出错: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"获取设备唯一ID时发生错误: {ex.Message}");
            }

            // 如果所有方法都失败，使用驱动器路径和卷标签作为最后的备选
            if (string.IsNullOrEmpty(uniqueId))
            {
                try
                {
                    DriveInfo drive = new DriveInfo(drivePath);
                    uniqueId = $"{drive.Name}_{drive.VolumeLabel}_{drive.TotalSize}";
                }
                catch
                {
                    uniqueId = Guid.NewGuid().ToString(); // 最坏情况下使用随机GUID
                }
            }

            return uniqueId;
        }
    }
}
