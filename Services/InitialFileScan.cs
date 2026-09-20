using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Services;

public class InitialFileScan
{
        /// <summary>启动全库扫描；返回本轮是否产生了数据库变更（新增/更新/删除，含去重删除）。</summary>
        public static async Task<bool> InitialScan(CancellationToken cancellationToken = default, IProgress<int>? progress = null)
        {
            var database = App.Services.GetRequiredService<MusicDatabaseService>();
            // 只枚举一次并保存路径，确定分母后复用快照扫描；不提前读取全部文件的元数据。
            var pending = new List<(string Path, List<string> Files)>();
            long total = 0;
            foreach (var folder in await database.GetFolders())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(folder.Path)) continue;
                // 外部导入虚拟行是哨兵路径不可枚举；其导入行在末尾统一做存在性对账。
                if (folder.IsExternalImport) continue;
                try
                {
                    var files = new List<string>();
                    foreach (var path in AddFolderService.EnumerateMusicPaths(folder.Path))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        files.Add(path);
                    }
                    pending.Add((folder.Path, files));
                    total += files.Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A missing drive or inaccessible subtree is not evidence that its music was deleted.
                    App.GetLogger<InitialFileScan>().LogWarning(ex, "启动扫描未完成，保留旧记录: {Path}", folder.Path);
                }
            }
            var progressGate = new object();
            long completed = 0;
            int lastPercent = 0;
            bool changed = false;
            progress?.Report(0);
            foreach (var folder in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int folderCompleted = 0;
                void Advance(int count)
                {
                    // 枚举未变更文件与提交元数据批次在不同线程；串行发布并限制为最多 100 次。
                    lock (progressGate)
                    {
                        folderCompleted += count;
                        completed += count;
                        int percent = total == 0 ? 99 : (int)Math.Min(99, completed * 100 / total);
                        if (percent <= lastPercent) return;
                        lastPercent = percent;
                        progress?.Report(percent);
                    }
                }
                try
                {
                    changed |= await database.ScanChangedFolderAsync(folder.Path, cancellationToken, folder.Files, Advance) > 0;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    App.GetLogger<InitialFileScan>().LogWarning(ex, "启动扫描未完成，保留旧记录: {Path}", folder.Path);
                    // 抛出前可能已提交部分批次，无法确认无变更：按有变更处理。
                    changed = true;
                }
                // 失败目录也结束本轮尝试；取消时不继续推进或发布完成。
                Advance(folder.Files.Count - folderCompleted);
                folder.Files.Clear();
            }
            changed |= await Deduplication(cancellationToken);
            // 外部导入对账：删除磁盘上确认缺失的导入行（盘根不可达整组保留）。
            changed |= await database.ReconcileExternalImportsAsync(cancellationToken);
            return changed;
        }

        /// <summary>按路径去重；返回是否删除了重复记录。</summary>
        public static async Task<bool> Deduplication(CancellationToken cancellationToken = default)
        {
            var database = App.Services.GetRequiredService<MusicDatabaseService>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var duplicates = new List<Music>();
            foreach (var music in await database.GetMusicListAsync())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.Add(music.Path)) duplicates.Add(music);
            }
            if (duplicates.Count == 0) return false;
            await database.DeletedMusicList(duplicates, cancellationToken);
            return true;
        }
}
