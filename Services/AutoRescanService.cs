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
            Dictionary<string, WatchedFolderChanges?> changes, CancellationToken cancellationToken)
        {
            using var lease = await LibraryOperationGate.EnterAsync(cancellationToken, reportBusy: false);
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
                // A cancelled later batch can still have earlier committed changes.
                if (committed) await database.PublishLibrarySnapshotAsync(default, preserveRows: true);
            }
        }
    }
}
