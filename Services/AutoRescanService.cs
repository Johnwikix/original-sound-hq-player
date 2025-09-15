using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static Dictionary<string, SubFolder> _subFoldersDict = new Dictionary<string, SubFolder>(1024);
        //private static List<SubFolder> subFolderList = new List<SubFolder>();
        //public static List<SubFolder> RecordInitialFolderTimes(string folder, int folderId)
        //{           
        //    SubFolder folderItem = new SubFolder
        //    {
        //        Path = folder,
        //        LastModifiedTime = Directory.GetLastWriteTime(folder),
        //        FolderId=folderId
        //    };
        //    subFolderList.Add(folderItem);
        //    Debug.WriteLine($"Folder: {folderItem.Path}, Last Modified Time: {folderItem.LastModifiedTime}，FolderId:{folderItem.FolderId}");
        //    // 递归获取子文件夹
        //    string[] subFolders = Directory.GetDirectories(folder);
        //    foreach (string subFolderItem in subFolders)
        //    {
        //        RecordInitialFolderTimes(subFolderItem, folderId);
        //    }
        //    return subFolderList;
        //}

        public static List<SubFolder> RecordInitialFolderTimes(string folder, int folderId)
        {
            // 创建方法内的局部变量，每次调用都会创建新的列表
            List<SubFolder> result = new List<SubFolder>();

            // 调用辅助方法进行递归收集
            CollectFolderInfo(folder, folderId, result);

            return result;
        }

        // 添加一个辅助方法来递归收集文件夹信息
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
                        // 3. 使用HashSet记录需要删除的路径，避免重复Any()调用
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

                        // 处理删除 - 使用HashSet避免Any()调用
                        var currentPaths = _subFoldersDict.Keys.ToHashSet();
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

                // 移出循环，减少不必要的UI更新
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

        //    public static async Task AutoScan()
        //    {
        //        try
        //        {
        //            List<Folder> folders = await MusicDatabaseService.GetFolders();
        //            int changeCount = 0;
        //            foreach (Folder folder in folders)
        //            {
        //                //subFolderList.Clear();
        //                List<SubFolder> subFolders = [];
        //                await Task.Run(() =>
        //                {
        //                    subFolders = RecordInitialFolderTimes(folder.Path, folder.Id);
        //                });
        //                List<SubFolder> subFoldersInDb = await MusicDatabaseService.GetSubFolders(folder.Id);
        //                if (subFoldersInDb != null && subFoldersInDb.Count > 0)
        //                {
        //                    foreach (SubFolder subFolder in subFolders)
        //                    {
        //                        if (!subFoldersInDb.Any(dbSubFolder => dbSubFolder.Path == subFolder.Path))
        //                        {
        //                            await MusicDatabaseService.AddSubFolder(subFolder);
        //                            await MusicDatabaseService.RescanFolderByPath(subFolder.Path, false, true);
        //                            Debug.WriteLine($"Added new subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.FolderId}");
        //                            changeCount++;
        //                        }
        //                        else
        //                        {
        //                            SubFolder dbSubFolder = subFoldersInDb.First(dbSubFolder => dbSubFolder.Path == subFolder.Path);
        //                            if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
        //                            {
        //                                dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
        //                                await MusicDatabaseService.UpdateSubFolder(dbSubFolder);
        //                                await MusicDatabaseService.RescanFolderByPath(subFolder.Path, false, true);
        //                                Debug.WriteLine($"Updated subfolder: {subFolder.Path},time:{subFolder.LastModifiedTime},folderId:{subFolder.FolderId}");
        //                                changeCount++;
        //                            }
        //                        }
        //                    }

        //                    // 处理删除
        //                    foreach (SubFolder dbSubFolder in subFoldersInDb)
        //                    {
        //                        if (!subFolders.Any(subFolder => subFolder.Path == dbSubFolder.Path))
        //                        {
        //                            await MusicDatabaseService.DeleteSubFolder(dbSubFolder);
        //                            await MusicDatabaseService.DeleteSubFolderByPath(dbSubFolder.Path);
        //                            Debug.WriteLine($"Deleted subfolder: {dbSubFolder.Path},time:{dbSubFolder.LastModifiedTime},folderId:{dbSubFolder.FolderId}");
        //                        }
        //                    }
        //                }
        //                else
        //                {
        //                    await MusicDatabaseService.InsertSubFolders(subFolders);
        //                }
        //                if (changeCount > 0)
        //                {
        //                    AppData.allSongs = await MusicDatabaseService.GetMusicListAsync();
        //                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
        //                    {
        //                        App.MainWindow.UpdateMusicList();
        //                    });
        //                }
        //            }
        //        }
        //        catch (Exception ex)
        //        {
        //            Debug.WriteLine($"Error: {ex.Message}");
        //        }
        //    }
        //}}
    }    
}
