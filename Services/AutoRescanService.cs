using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static Dictionary<string, SubFolder> _subFoldersDict = new Dictionary<string, SubFolder>(1024);

        public static List<SubFolder> RecordInitialFolderTimes(string folder, int folderId)
        {
            List<SubFolder> result = new List<SubFolder>();
            CollectFolderInfo(folder, folderId, result);
            return result;
        }
        private static void CollectFolderInfo(string folder, int folderId, List<SubFolder> folderList)
        {
            try
            {
                // 添加当前文件夹
                SubFolder folderItem = new SubFolder
                {
                    Path = folder,
                    LastModifiedTime = Directory.GetLastWriteTime(folder),
                    FolderId = folderId
                };
                folderList.Add(folderItem);
                Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.FolderId}");

                // 递归获取子文件夹
                string[] subFolders = Directory.GetDirectories(folder);
                foreach (string subFolderItem in subFolders)
                {
                    CollectFolderInfo(subFolderItem, folderId, folderList);
                }
            }
            catch (Exception ex)
            {
                // 安全处理可能的异常（如权限问题或文件夹不存在）
                Debug.WriteLine($"文件夹扫描错误 {folder}: {ex.Message}");
            }
        }

        public static async Task AutoScan()
        {
            try
            {
                var folders = await MusicDatabaseService.GetFolders();
                int changeCount = 0;

                foreach (var folder in folders)
                {
                    _subFoldersDict.Clear(); // 重用Dictionary

                    var subFolders = await Task.Run(() =>
                    {
                        return RecordInitialFolderTimes(folder.Path, folder.Id);
                    });

                    foreach (var subFolder in subFolders)
                    {
                        _subFoldersDict[subFolder.Path] = subFolder;
                    }

                    var subFoldersInDb = await MusicDatabaseService.GetSubFolders(folder.Id);
                    if (subFoldersInDb?.Count > 0)
                    {
                        var dbPaths = new HashSet<string>(subFoldersInDb.Count);
                        foreach (var dbSubFolder in subFoldersInDb)
                        {
                            dbPaths.Add(dbSubFolder.Path);
                        }

                        // 处理新增和更新
                        foreach (var kvp in _subFoldersDict)
                        {
                            var path = kvp.Key;
                            var subFolder = kvp.Value;

                            if (!dbPaths.Contains(path))
                            {
                                await MusicDatabaseService.AddSubFolder(subFolder);
                                await MusicDatabaseService.RescanFolderWithOutUpdateAll(path, true);
                                changeCount++;
                            }
                            else
                            {
                                SubFolder dbSubFolder = subFoldersInDb.AsValueEnumerable().First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
                                if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                                {
                                    dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                    await MusicDatabaseService.UpdateSubFolder(dbSubFolder);
                                    await MusicDatabaseService.RescanFolderWithOutUpdateAll(subFolder.Path, true);
                                    changeCount++;
                                }
                            }
                        }

                        var currentPaths = _subFoldersDict.Keys.AsValueEnumerable().ToHashSet();
                        foreach (var dbSubFolder in subFoldersInDb)
                        {
                            if (!currentPaths.Contains(dbSubFolder.Path))
                            {
                                await MusicDatabaseService.DeleteSubFolder(dbSubFolder);
                                await MusicDatabaseService.DeleteSubFolderByPath(dbSubFolder.Path);
                                changeCount++;
                            }
                        }
                    }
                    else
                    {
                        await MusicDatabaseService.InsertSubFolders(subFolders);
                        changeCount++;
                    }
                }

                if (changeCount > 0)
                {
                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        App.MainWindow.UpdateMusicList();
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error: {ex.Message}");
            }
        }
    }
}
