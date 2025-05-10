using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class UsbDeviceSubFolderRescan
    {
        //private  List<UsbDeviceSubFolder> usbDeviceSubFolders = new List<UsbDeviceSubFolder>();
        //public  List<UsbDeviceSubFolder> RecordInitialFolderTimes(string folder, string uniqueDeviceId)
        //{
        //    UsbDeviceSubFolder folderItem = new UsbDeviceSubFolder
        //    {
        //        Path = folder,
        //        LastModifiedTime = Directory.GetLastWriteTime(folder),
        //        UniqueDeviceId = uniqueDeviceId
        //    };
        //    usbDeviceSubFolders.Add(folderItem);
        //    Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.UniqueDeviceId}");
        //    // 递归获取子文件夹
        //    string[] subFolders = Directory.GetDirectories(folder);
        //    foreach (string subFolderItem in subFolders)
        //    {
        //        RecordInitialFolderTimes(subFolderItem, uniqueDeviceId);
        //    }
        //    return usbDeviceSubFolders;
        //}
        public List<UsbDeviceSubFolder> RecordInitialFolderTimes(string folder, string uniqueDeviceId)
        {
            // 创建本地列表，而不是使用类成员变量
            List<UsbDeviceSubFolder> result = new List<UsbDeviceSubFolder>();

            // 递归收集文件夹信息
            CollectFolderInfo(folder, uniqueDeviceId, result);

            return result;
        }

        // 添加一个辅助方法来递归收集文件夹信息
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
                Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.UniqueDeviceId}");

                // 递归获取子文件夹
                string[] subFolders = Directory.GetDirectories(folder);
                foreach (string subFolderItem in subFolders)
                {
                    CollectFolderInfo(subFolderItem, uniqueDeviceId, folderList);
                }
            }
            catch (Exception ex)
            {
                // 安全处理可能的异常（如权限问题或文件夹不存在）
                Debug.WriteLine($"文件夹扫描错误 {folder}: {ex.Message}");
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
                List<UsbDeviceSubFolder> subFoldersInDb = await MusicDatabaseService.GetUsbDeviceSubFolders(uniqueDeviceId);
                if (subFoldersInDb != null && subFoldersInDb.Count > 0)
                {
                    foreach (UsbDeviceSubFolder subFolder in subFolders)
                    {
                        if (!subFoldersInDb.Any(dbSubFolder => dbSubFolder.Path == subFolder.Path))
                        {
                            await MusicDatabaseService.AddUsbDeviceSubFolder(subFolder);
                            await MusicDatabaseService.RescanUsbDeviceFolderByPath(usbDeviceMusics, uniqueDeviceId, subFolder.Path, true);
                            Debug.WriteLine($"Added new subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.UniqueDeviceId}");
                            changeCount++;
                        }
                        else
                        {
                            UsbDeviceSubFolder dbSubFolder = subFoldersInDb.First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
                            if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                            {
                                dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                await MusicDatabaseService.UpdateUsbDeviceSubFolder(dbSubFolder);
                                await MusicDatabaseService.RescanUsbDeviceFolderByPath(usbDeviceMusics, uniqueDeviceId, subFolder.Path, true);
                                Debug.WriteLine($"Updated subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.UniqueDeviceId}");
                                changeCount++;
                            }
                        }
                    }

                    // 处理删除
                    foreach (UsbDeviceSubFolder dbSubFolder in subFoldersInDb)
                    {
                        if (!subFolders.Any(subFolder => subFolder.Path == dbSubFolder.Path))
                        {
                            await MusicDatabaseService.DeleteUsbDeviceSubFolder(dbSubFolder);
                            await MusicDatabaseService.DeleteUsbDeviceSubFolderByPath(dbSubFolder.Path, uniqueDeviceId);
                            Debug.WriteLine($"Deleted subfolder: {dbSubFolder.Path},time:{dbSubFolder.LastModifiedTime},folderId:{dbSubFolder.UniqueDeviceId}");
                        }
                    }
                }
                else
                {
                    await MusicDatabaseService.InsertUsbDeviceSubFolders(subFolders);
                }

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
