using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class UsbDeviceSubFolderRescan
    {
        private static ILogger<UsbDeviceSubFolderRescan> _logger = App.GetLogger<UsbDeviceSubFolderRescan>();

        public List<UsbDeviceSubFolder> RecordInitialFolderTimes(string folder, string uniqueDeviceId)
        {
            // 创建本地列表，而不是使用类成员变量
            List<UsbDeviceSubFolder> result = new List<UsbDeviceSubFolder>();

            // 递归收集文件夹信息
            CollectFolderInfo(folder, uniqueDeviceId, result);

            return result;
        }

        private void CollectFolderInfo(string folder, string uniqueDeviceId, List<UsbDeviceSubFolder> folderList)
        {
            try
            {
                // 添加当前文件夹
                UsbDeviceSubFolder folderItem = new UsbDeviceSubFolder
                {
                    Path = folder,
                    LastModifiedTime = Directory.GetLastWriteTime(folder),
                    UniqueDeviceId = uniqueDeviceId
                };
                folderList.Add(folderItem);

                // 递归获取子文件夹
                string[] subFolders = Directory.GetDirectories(folder);
                foreach (string subFolderItem in subFolders)
                {
                    CollectFolderInfo(subFolderItem, uniqueDeviceId, folderList);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"CollectFolderInfo 文件夹扫描错误 {folder}: {ex.Message}");
            }
        }

        public async Task UsbDeviceSubFolderAutoScan(List<UsbDeviceMusic> usbDeviceMusics, string folder, string uniqueDeviceId)
        {
            try
            {
                int changeCount = 0;
                List<UsbDeviceSubFolder> subFolders = new List<UsbDeviceSubFolder>();
                await Task.Run(() =>
                {
                    subFolders = RecordInitialFolderTimes(folder, uniqueDeviceId);
                });
                List<UsbDeviceSubFolder> subFoldersInDb = await App.Services.GetRequiredService<MusicDatabaseService>().GetUsbDeviceSubFolders(uniqueDeviceId);
                if (subFoldersInDb is not null && subFoldersInDb.Count > 0)
                {
                    foreach (UsbDeviceSubFolder subFolder in subFolders)
                    {
                        if (!subFoldersInDb.AsValueEnumerable().Any(dbSubFolder => dbSubFolder.Path == subFolder.Path))
                        {
                            await App.Services.GetRequiredService<MusicDatabaseService>().AddUsbDeviceSubFolder(subFolder);
                            await App.Services.GetRequiredService<MusicDatabaseService>().RescanUsbDeviceFolderByPath(usbDeviceMusics, uniqueDeviceId, subFolder.Path, true);
                            changeCount++;
                        }
                        else
                        {
                            UsbDeviceSubFolder dbSubFolder = subFoldersInDb.AsValueEnumerable().First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
                            if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                            {
                                dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                await App.Services.GetRequiredService<MusicDatabaseService>().UpdateUsbDeviceSubFolder(dbSubFolder);
                                await App.Services.GetRequiredService<MusicDatabaseService>().RescanUsbDeviceFolderByPath(usbDeviceMusics, uniqueDeviceId, subFolder.Path, true);
                                changeCount++;
                            }
                        }
                    }

                    // 处理删除
                    foreach (UsbDeviceSubFolder dbSubFolder in subFoldersInDb)
                    {
                        if (!subFolders.AsValueEnumerable().Any(subFolder => subFolder.Path == dbSubFolder.Path))
                        {
                            await App.Services.GetRequiredService<MusicDatabaseService>().DeleteUsbDeviceSubFolder(dbSubFolder);
                            await App.Services.GetRequiredService<MusicDatabaseService>().DeleteUsbDeviceSubFolderByPath(dbSubFolder.Path, uniqueDeviceId);
                        }
                    }
                }
                else
                {
                    await App.Services.GetRequiredService<MusicDatabaseService>().InsertUsbDeviceSubFolders(subFolders);
                }

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"UsbDeviceSubFolderAutoScan 错误: {ex.Message}");
            }
        }
    }
}
