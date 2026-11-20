using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services
{
    public class AutoRescanService
    {
        private static readonly ILogger<AutoRescanService> _logger = App.GetLogger<AutoRescanService>();
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

        internal static async Task AutoScan(MusicDatabaseService database,
            Dictionary<string, HashSet<string>?> changes, CancellationToken cancellationToken)
        {
            using var lease = await LibraryOperationGate.EnterAsync(cancellationToken, reportBusy: false);
            Interlocked.Increment(ref _activeScans);
            bool committed = false;
            void OnCommitted() => committed = true;

            try
            {
                // Re-read roots under the maintenance lease: a queued event may outlive a removed root.
                foreach (var folder in await database.GetFolders())
                {
                    if (folder.IsExternalImport || !changes.TryGetValue(folder.Path, out var paths)) continue;
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        await database.ReconcileWatchedFolderAsync(folder.Path, paths, OnCommitted, cancellationToken);
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
                    // Disabling the watcher can cancel later batches, but already committed rows
                    // must still reach the live library. The publisher guards application shutdown.
                    if (committed) await database.PublishLibrarySnapshotAsync(default, preserveRows: true);
                }
                finally { Interlocked.Decrement(ref _activeScans); }
            }
        }
    }
}
