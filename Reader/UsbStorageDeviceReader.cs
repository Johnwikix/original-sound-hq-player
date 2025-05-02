using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
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
                                var deviceInfoObj = new UsbStorageDevice
                                {
                                    Path = drive.Name,
                                    Name = drive.VolumeLabel,
                                    FreeSpaceInGB = Math.Round((double)drive.AvailableFreeSpace / (1024 * 1024 * 1024))
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
    }
}
