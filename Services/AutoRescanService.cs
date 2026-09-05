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
        private static Dictionary<string, SubFolder> _subFoldersDict = new Dictionary<string, SubFolder>(512);
        private static ILogger<AutoRescanService> _logger = App.GetLogger<AutoRescanService>();
        private static int _activeScans;

        /// <summary>是否有 AutoScan 正在执行。刷新音乐库前等待其归零，避免读到扫描中间态。</summary>
        public static bool AnyActive => Volatile.Read(ref _activeScans) > 0;

        /// <summary>轮询等待所有在飞 AutoScan 结束（超时放行兜底，防异常挂起卡死 UI 刷新）。</summary>
        public static async Task WaitUntilIdleAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (AnyActive)
            {
                if (DateTime.UtcNow >= deadline) return;
                await Task.Delay(100, cancellationToken);
            }
        }

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
                string[] subFolders = Directory.GetDirectories(folder);

                SubFolder folderItem = new SubFolder
                {
                    Path = folder,
                    LastModifiedTime = Directory.GetLastWriteTime(folder),
                    FolderId = folderId
                };
                folderList.Add(folderItem);

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
            Interlocked.Increment(ref _activeScans);
            try
            {
                var dbService = App.Services.GetRequiredService<MusicDatabaseService>();
                var folders = await dbService.GetFolders();
                bool needRefresh = false;

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
                                int added = await dbService.RescanFolderWithOutUpdateAll(path, true);
                                if (added > 0) needRefresh = true;
                            }
                            else
                            {
                                var dbSubFolder = subFoldersInDb.AsValueEnumerable()
                                    .First(f => f.Path == subFolder.Path);

                                if (dbSubFolder.LastModifiedTime != subFolder.LastModifiedTime)
                                {
                                    dbSubFolder.LastModifiedTime = subFolder.LastModifiedTime;
                                    await dbService.UpdateSubFolder(dbSubFolder);
                                    int added = await dbService.RescanFolderWithOutUpdateAll(subFolder.Path, true);
                                    if (added > 0) needRefresh = true;
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
                                needRefresh = true;
                            }
                        }
                    }
                    else
                    {
                        await dbService.InsertSubFolders(subFolders);
                        needRefresh = true;
                    }
                }

                if (needRefresh)
                    await App.Services.GetRequiredService<AppViewModel>().RefreshSongsSourceAsync();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"AutoScan 错误: {ex.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeScans);
            }
        }
    }
}
