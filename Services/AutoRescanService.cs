using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static int _scanQueued;
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
            var result = new List<SubFolder>();
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = false,
                AttributesToSkip = FileAttributes.ReparsePoint
            };
            result.Add(new SubFolder { Path = folder, FolderId = folderId, LastModifiedTime = Directory.GetLastWriteTime(folder) });
            foreach (var path in Directory.EnumerateDirectories(folder, "*", options))
                result.Add(new SubFolder { Path = path, FolderId = folderId, LastModifiedTime = Directory.GetLastWriteTime(path) });
            return result;
        }

        public static async Task AutoScan(CancellationToken cancellationToken = default)
        {
            // Coalesce watcher requests, but wait for manual maintenance instead of dropping its pending scan.
            if (Interlocked.Exchange(ref _scanQueued, 1) != 0) return;
            try
            {
                using var lease = await LibraryOperationGate.EnterAsync(cancellationToken);
                Interlocked.Increment(ref _activeScans);
                bool refresh = false;
                try
                {
                    var database = App.Services.GetRequiredService<MusicDatabaseService>();
                    foreach (var folder in await database.GetFolders())
                    {
                        // 虚拟行无磁盘根可枚举；导入行由启动对账与虚拟行手动重扫维护。
                        if (folder.IsExternalImport) continue;
                        cancellationToken.ThrowIfCancellationRequested();
                        try
                        {
                            // Full enumeration must succeed before inferring removed directories.
                            var current = await Task.Run(() => RecordInitialFolderTimes(folder.Path, folder.Id), cancellationToken);
                            var previous = new Dictionary<string, SubFolder>(StringComparer.OrdinalIgnoreCase);
                            foreach (var item in await database.GetSubFolders(folder.Id)) previous.TryAdd(item.Path, item);
                            foreach (var item in current)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                previous.Remove(item.Path, out var old);
                                if (old is not null && old.LastModifiedTime == item.LastModifiedTime) continue;
                                refresh = true; // A failed scan can still have committed earlier batches.
                                await database.RescanFolderWithOutUpdateAll(item.Path, true);
                                // Advance the timestamp only after successful reconciliation, so errors remain retryable.
                                if (old is null) await database.AddSubFolder(item);
                                else
                                {
                                    old.LastModifiedTime = item.LastModifiedTime;
                                    await database.UpdateSubFolder(old);
                                }
                            }
                            foreach (var removed in previous.Values)
                            {
                                refresh = true;
                                await database.DeleteSubFolderByPath(removed.Path);
                                await database.DeleteSubFolder(removed);
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogWarning(ex, "自动扫描未完成，保留未确认的旧记录: {Path}", folder.Path);
                        }
                    }
                }
                finally
                {
                    try
                    {
                        if (refresh) await App.Services.GetRequiredService<AppViewModel>().RefreshSongsSourceAsync();
                    }
                    finally { Interlocked.Decrement(ref _activeScans); }
                }
            }
            catch (OperationCanceledException) { }
            finally { Volatile.Write(ref _scanQueued, 0); }
        }
    }
}
