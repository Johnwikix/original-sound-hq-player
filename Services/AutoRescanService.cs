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
            List<Folder> folders = await MusicDatabaseService.GetFolders();
            foreach (Folder folder in folders) {
                List<SubFolder> subFolders = RecordInitialFolderTimes(folder.Path, folder.Id);
                List<SubFolder> subFoldersInDb = await MusicDatabaseService.GetSubFolders(folder.Id);
                if (subFoldersInDb != null && subFoldersInDb.Count > 0)
                {
                    foreach (SubFolder subFolder in subFolders)
                    {
                        if (!subFoldersInDb.Any(dbSubFolder => dbSubFolder.Path == subFolder.Path))
                        {
                            await MusicDatabaseService.AddSubFolder(subFolder);
                            await MusicDatabaseService.RescanFolderByPath(subFolder.Path);
                            Debug.WriteLine($"Added new subfolder: {subFolder.Path}");
                        }
                        else
                        {
                            SubFolder dbSubFolder = subFoldersInDb.First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
                            if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                            {
                                dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                await MusicDatabaseService.UpdateSubFolder(dbSubFolder);
                                await MusicDatabaseService.RescanFolderByPath(subFolder.Path);
                                Debug.WriteLine($"Updated subfolder: {subFolder.Path}");
                            }
                        }
                    }

                    // 处理删除
                    foreach (SubFolder dbSubFolder in subFoldersInDb)
                    {
                        if (!subFolders.Any(subFolder => subFolder.Path == dbSubFolder.Path))
                        {
                            await MusicDatabaseService.DeleteSubFolder(dbSubFolder);
                            Debug.WriteLine($"Deleted subfolder: {dbSubFolder.Path}");
                        }
                    }
                }
                else {
                    await MusicDatabaseService.InsertSubFolders(subFolders);
                }                
            }
        }
    }
}
