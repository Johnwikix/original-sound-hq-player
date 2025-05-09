using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class UsbDeviceSubFolderRescan
    {
        private  List<UsbDeviceSubFolder> usbDeviceSubFolders = new List<UsbDeviceSubFolder>();
        public  List<UsbDeviceSubFolder> RecordInitialFolderTimes(string folder, string uniqueDeviceId)
        {
            UsbDeviceSubFolder folderItem = new UsbDeviceSubFolder
            {
                Path = folder,
                LastModifiedTime = Directory.GetLastWriteTime(folder),
                UniqueDeviceId = uniqueDeviceId
            };
            usbDeviceSubFolders.Add(folderItem);
            Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.UniqueDeviceId}");
            // 递归获取子文件夹
            string[] subFolders = Directory.GetDirectories(folder);
            foreach (string subFolderItem in subFolders)
            {
                RecordInitialFolderTimes(subFolderItem, uniqueDeviceId);
            }
            return usbDeviceSubFolders;
        }

        public async Task UsbDeviceSubFolderAutoScan(List<UsbDeviceMusic> usbDeviceMusics,string folder, string uniqueDeviceId)
        {
            try
            {
                int changeCount = 0;
                usbDeviceSubFolders.Clear();
                List<UsbDeviceSubFolder> subFolders = RecordInitialFolderTimes(folder, uniqueDeviceId);
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
                //if (changeCount > 0)
                //{
                //    var mainWindow = (App.MainWindow as MainWindow);
                //    if (mainWindow != null)
                //    {
                //        mainWindow.DispatcherQueue.TryEnqueue(() =>
                //        {
                //            mainWindow.UpdateMusicList();
                //        });
                //    }
                //}

            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
