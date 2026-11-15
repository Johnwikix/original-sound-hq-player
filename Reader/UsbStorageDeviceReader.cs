using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Devices.Portable;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Reader
{
    public class UsbStorageDeviceReader
    {
        private static readonly ILogger<UsbStorageDeviceReader> _logger = App.GetLogger<UsbStorageDeviceReader>();
        // 发现、扫描和写入共享同一条 MTP 通道，避免设备驱动同时处理相互交错的请求。
        internal static SemaphoreSlim PortableIoGate { get; } = new(1, 1);

        public static async Task<List<UsbStorageDevice>> GetUsbStorageDevicesAsync()
        {
            var usbDevices = new List<UsbStorageDevice>();
            // 有盘符的磁盘和 MTP 存储是两种地址，不能用 DriveInfo 过滤后者。
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType == DriveType.Removable && drive.IsReady)
                        {
                            usbDevices.Add(new UsbStorageDevice
                            {
                                Path = drive.Name,
                                Name = drive.VolumeLabel,
                                FreeSpaceInGB = ToGigabytes(drive.AvailableFreeSpace),
                                TotalSpaceInGB = ToGigabytes(drive.TotalSize),
                                UniqueId = await GetDeviceUniqueIdAsync(drive.Name)
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读取可移动磁盘失败: {Drive}", drive.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetUsbStorageDevicesAsync 获取USB存储设备失败: {ex.Message}");
            }

            await PortableIoGate.WaitAsync();
            try
            {
                var portableDevices = await DeviceInformation.FindAllAsync(StorageDevice.GetDeviceSelector());
                foreach (var info in portableDevices)
                {
                    try
                    {
                        var root = StorageDevice.FromId(info.Id);
                        // 带盘符的设备已在上面枚举，手机则需要选择其内部存储或 SD 卡。
                        if (!string.IsNullOrEmpty(root.Path)) continue;
                        foreach (var storage in await root.GetFoldersAsync())
                        {
                            var device = new UsbStorageDevice
                            {
                                Name = $"{info.Name} / {storage.Name}",
                                UniqueId = $"MTP:{info.Id}:{storage.FolderRelativeId}",
                                PortableDeviceId = info.Id,
                                PortableStorageId = storage.FolderRelativeId
                            };
                            try
                            {
                                var properties = await storage.Properties.RetrievePropertiesAsync(
                                    new[] { "System.FreeSpace", "System.Capacity" });
                                if (properties.TryGetValue("System.FreeSpace", out var free) && free is not null)
                                    device.FreeSpaceInGB = ToGigabytes(Convert.ToDouble(free));
                                if (properties.TryGetValue("System.Capacity", out var capacity) && capacity is not null)
                                    device.TotalSpaceInGB = ToGigabytes(Convert.ToDouble(capacity));
                            }
                            catch (Exception ex)
                            {
                                _logger.LogDebug(ex, "读取便携式存储容量失败: {Device}", info.Name);
                            }
                            usbDevices.Add(device);
                        }
                    }
                    catch (Exception ex)
                    {
                        // 锁屏、未授权或插拔中的一台设备不能影响其他设备。
                        _logger.LogWarning(ex, "读取便携式存储失败: {Device}", info.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "枚举便携式存储设备失败");
            }
            finally
            {
                PortableIoGate.Release();
            }
            return usbDevices;
        }

        private static double ToGigabytes(double bytes) => Math.Round(bytes / (1024 * 1024 * 1024));

        public static async Task<StorageFolder> OpenPortableStorageAsync(UsbStorageDevice device)
        {
            var root = StorageDevice.FromId(device.PortableDeviceId ?? throw new ArgumentException("不是便携式存储设备", nameof(device)));
            foreach (var folder in await root.GetFoldersAsync())
            {
                if (folder.FolderRelativeId == device.PortableStorageId) return folder;
            }
            throw new DirectoryNotFoundException($"便携式存储已不可用: {device.Name}");
        }

        private static async Task<string> GetDeviceUniqueIdAsync(string drivePath)
        {
            try
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(drivePath);
                IDictionary<string, object> properties = await folder.Properties.RetrievePropertiesAsync(
                    new List<string> { "System.Devices.DeviceInstanceId" });

                if (properties.TryGetValue("System.Devices.DeviceInstanceId", out var instanceIdObj) &&
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
