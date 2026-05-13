using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static Dictionary<string, SubFolder> _subFoldersDict = new Dictionary<string, SubFolder>(1024);
        private static ILogger<AutoRescanService> _logger = App.GetLogger<AutoRescanService>();

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

                // 递归获取子文件夹
                string[] subFolders = Directory.GetDirectories(folder);
                foreach (string subFolderItem in subFolders)
                {
                    CollectFolderInfo(subFolderItem, folderId, folderList);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"CollectFolderInfo 文件夹扫描错误 {folder}: {ex.Message}");
            }
        }

        public static async Task AutoScan(CancellationToken cancellationToken = default)
        {
            try
            {
                var dbService = App.Services.GetRequiredService<MusicDatabaseService>();
                var folders = await dbService.GetFolders();
                int changeCount = 0;

                foreach (var folder in folders)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    _subFoldersDict.Clear();

                    var subFolders = await Task.Run(
                        () => RecordInitialFolderTimes(folder.Path, folder.Id),
                        cancellationToken);

                    foreach (var subFolder in subFolders)
                        _subFoldersDict[subFolder.Path] = subFolder;

                    var subFoldersInDb = await dbService.GetSubFolders(folder.Id);

                    if (subFoldersInDb?.Count > 0)
                    {
                        var dbPaths = new HashSet<string>(subFoldersInDb.Count);
                        foreach (var dbSubFolder in subFoldersInDb)
                            dbPaths.Add(dbSubFolder.Path);

                        // 处理新增和更新
                        foreach (var kvp in _subFoldersDict)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            var path = kvp.Key;
                            var subFolder = kvp.Value;

                            if (!dbPaths.Contains(path))
                            {
                                await dbService.AddSubFolder(subFolder);
                                await dbService.RescanFolderWithOutUpdateAll(path, true);
                                changeCount++;
                            }
                            else
                            {
                                var dbSubFolder = subFoldersInDb.AsValueEnumerable()
                                    .First(f => f.Path == subFolder.Path);

                                if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                                {
                                    dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                    await dbService.UpdateSubFolder(dbSubFolder);
                                    await dbService.RescanFolderWithOutUpdateAll(subFolder.Path, true);
                                    changeCount++;
                                }
                            }
                        }

                        // 处理删除
                        var currentPaths = _subFoldersDict.Keys.AsValueEnumerable().ToHashSet();
                        foreach (var dbSubFolder in subFoldersInDb)
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            if (!currentPaths.Contains(dbSubFolder.Path))
                            {
                                await dbService.DeleteSubFolder(dbSubFolder);
                                await dbService.DeleteSubFolderByPath(dbSubFolder.Path);
                                changeCount++;
                            }
                        }
                    }
                    else
                    {
                        await dbService.InsertSubFolders(subFolders);
                        changeCount++;
                    }
                }

                if (changeCount > 0)
                    App.Services.GetRequiredService<AppViewModel>().RefreshSongsSource();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AutoScan 错误: {ex.Message}");
            }
        }
    }
}
