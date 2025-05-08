using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.Devices.Gpio;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static List<SubFolder> subFolderList = new List<SubFolder>();
        public static List<SubFolder> RecordInitialFolderTimes(string folder, int folderId)
        {           
            SubFolder folderItem = new SubFolder
            {
                Path = folder,
                LastModifiedTime = Directory.GetLastWriteTime(folder),
                FolderId=folderId
            };
            subFolderList.Add(folderItem);
            Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.FolderId}");
            // 递归获取子文件夹
            string[] subFolders = Directory.GetDirectories(folder);
            foreach (string subFolderItem in subFolders)
            {
                RecordInitialFolderTimes(subFolderItem, folderId);
            }
            return subFolderList;
        }

        public static async Task AutoScan() {
            //await MusicDatabaseService.DeleteAllSubFolder();
            List<Folder> folders = await MusicDatabaseService.GetFolders();
            int changeCount = 0;
            foreach (Folder folder in folders) {
                subFolderList.Clear();
                List<SubFolder> subFolders = RecordInitialFolderTimes(folder.Path, folder.Id);
                List<SubFolder> subFoldersInDb = await MusicDatabaseService.GetSubFolders(folder.Id);
                if (subFoldersInDb != null && subFoldersInDb.Count > 0)
                {
                    foreach (SubFolder subFolder in subFolders)
                    {
                        if (!subFoldersInDb.Any(dbSubFolder => dbSubFolder.Path == subFolder.Path))
                        {
                            await MusicDatabaseService.AddSubFolder(subFolder);
                            await MusicDatabaseService.RescanFolderByPath(subFolder.Path,false);
                            Debug.WriteLine($"Added new subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.FolderId}");
                            changeCount++;
                        }
                        else
                        {
                            SubFolder dbSubFolder = subFoldersInDb.First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
                            if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                            {
                                dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                await MusicDatabaseService.UpdateSubFolder(dbSubFolder);
                                await MusicDatabaseService.RescanFolderByPath(subFolder.Path, false);
                                Debug.WriteLine($"Updated subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.FolderId}");
                                changeCount++;
                            }
                        }
                    }

                    // 处理删除
                    foreach (SubFolder dbSubFolder in subFoldersInDb)
                    {
                        if (!subFolders.Any(subFolder => subFolder.Path == dbSubFolder.Path))
                        {
                            await MusicDatabaseService.DeleteSubFolder(dbSubFolder);
                            Debug.WriteLine($"Deleted subfolder: {dbSubFolder.Path},time:{dbSubFolder.LastModifiedTime},folderId:{dbSubFolder.FolderId}");
                        }
                    }
                }
                else {
                    await MusicDatabaseService.InsertSubFolders(subFolders);
                }
                if (changeCount > 0) {
                    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
                    var mainWindow = (App.MainWindow as MainWindow);
                    if (mainWindow != null)
                    {
                        mainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            mainWindow.UpdateMusicList();
                        });
                    }
                }
            }
        }
    }
}
