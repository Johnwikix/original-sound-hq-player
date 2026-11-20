using System;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using FileAttributes = System.IO.FileAttributes;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
        private sealed class DirectorySongCount
        {
            public string Path { get; set; } = "";
            public int SongCount { get; set; }
        }

        public async Task<List<Folder>> GetFoldersWithSongCountsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var folders = await GetFolders();
            cancellationToken.ThrowIfCancellationRequested();
            // The persisted Folder.SongCount can be stale in older databases. Count actual songs,
            // grouping once by directory rather than materializing every Music or relying on UI startup order.
            var counts = await _dbConnection.QueryAsync<DirectorySongCount>(
                "SELECT FolderPath AS Path, COUNT(*) AS SongCount FROM Music WHERE SourceId = 0 AND IsDetached=0 AND IsMissing=0 GROUP BY FolderPath COLLATE NOCASE");
            cancellationToken.ThrowIfCancellationRequested();
            // 外部导入虚拟行的计数同源现算：归属 = 分组目录不落入任何本地扫描根。
            Folder? external = null;
            var roots = new List<Folder>(folders.Count);
            foreach (var folder in folders)
            {
                if (folder.IsExternalImport) { external = folder; continue; }
                roots.Add(folder);
                folder.SongCount = 0;
            }
            if (external is not null) external.SongCount = 0;
            foreach (var count in counts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool ownedByRoot = false;
                foreach (var folder in roots)
                {
                    if (LibraryPath.IsWithin(count.Path, folder.Path))
                    {
                        // 嵌套扫描根的历史库沿用原口径：各根分别累计，不提前 break。
                        folder.SongCount += count.SongCount;
                        ownedByRoot = true;
                    }
                }
                if (external is not null && !ownedByRoot) external.SongCount += count.SongCount;
            }
            return folders;
        }

        public async Task ScanFolderAsync(StorageFolder folder, int folderId,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted = null)
        {
            // Read paths only, not every song's metadata/strings. This snapshot is immutable while workers run.
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            var existingPaths = new HashSet<string>(
                await _dbConnection.QueryScalarsAsync<string>("SELECT Path FROM Music WHERE SourceId=0 AND IsDetached=0 AND IsMissing=0 AND (FolderPath=? COLLATE NOCASE OR substr(FolderPath,1,length(?))=? COLLATE NOCASE)", root, prefix, prefix), StringComparer.OrdinalIgnoreCase);
            await addFolderService.ScanPathsAsync(
                AddFolderService.EnumerateMusicPaths(folder.Path).Where(path => !existingPaths.Contains(path)),
                batch => CommitScanBatchAsync(batch, onBatchInserted));
            // Do not walk the entire tree before delivering the first song.
            await InsertSubFolders(AutoRescanService.RecordInitialFolderTimes(folder.Path, folderId));
        }

        /// <summary>提交一批扫描结果；返回新增行（供增量 UI 回调复用）与 UPDATE 命中的行数——
        /// 两者都属于数据库变更，调用方据此决定是否刷新视图。</summary>
        internal async Task<(List<Music> Added, int Updated)> CommitScanBatchAsync(IReadOnlyList<(Music Music, string Lyrics)> batch,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted, Action? onCommitted = null, bool replaceExistingPath = false)
        {
            var parser = new WinUIMusicPlayer.Services.Lyrics.LyricsParser();
            var embedded = new Dictionary<Music, LyricsDocument>();
            foreach (var entry in batch)
            {
                if (entry.Music is null || string.IsNullOrWhiteSpace(entry.Lyrics)) continue;
                try { embedded[entry.Music] = parser.Import(entry.Lyrics); }
                catch (Exception ex) when (ex is FormatException or OverflowException or System.Xml.XmlException or System.Text.RegularExpressions.RegexMatchTimeoutException) { _logger.LogWarning(ex, "Embedded lyrics could not be imported: {Path}", entry.Music.Path); }
            }
            var added = new List<Music>(batch.Count);
            int updated = 0;
            await _dbConnection.RunInTransactionAsync(db =>
            {
                foreach (var (music, lyrics) in batch)
                {
                    if (music is null) continue;
                    var existing = music.Id == 0
                        ? db.FindWithQuery<Music>("SELECT Id,IsDetached,IsMissing FROM Music WHERE Path=? COLLATE NOCASE ORDER BY IsDetached,Id LIMIT 1", music.Path)
                        : db.FindWithQuery<Music>("SELECT Id,IsDetached,IsMissing FROM Music WHERE Id=?", music.Id);
                    if (music.Id == 0 && !replaceExistingPath && existing is { IsDetached: false, IsMissing: false }) continue;
                    if (existing is not null) music.Id = existing.Id;
                    // 延迟写入时保留数据库标签，重新纳入的文件仍可恢复可见性。
                    if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE", music.Path) != 0)
                    {
                        if (existing is { IsDetached: true } or { IsMissing: true })
                        {
                            db.Execute("UPDATE Music SET IsDetached=0,IsMissing=0 WHERE Id=?", music.Id);
                            if (existing.IsDetached) added.Add(db.Get<Music>(music.Id));
                            else updated++;
                        }
                        continue;
                    }
                    if (music.Id == 0)
                    {
                        db.Insert(music);
                        MusicMetadataStorage.AttachSong(db, music);
                        added.Add(music);
                    }
                    else
                    {
                        // Update only metadata; favorite/order/play count/lyrics offsets may change during a scan.
                        int changed = db.Execute("UPDATE Music SET Title=?, TitleFromFileName=?, Author=?, Album=?, Duration=?, FolderPath=?, LastLevelFolderPath=?, " +
                            "Path=?, Extension=?, BitDepth=?, BitRate=?, SampleRate=?, Channel=?, TrackNumber=?, DiskNumber=?, Year=?, " +
                            "CreateTime=?, UpdateTime=?,CodecId=?,EncodingKind=?,IsDetached=0,IsMissing=0,MetadataRevision=MetadataRevision+1 WHERE Id=?",
                            music.Title, music.TitleFromFileName, music.Author, music.Album, music.Duration, music.FolderPath, music.LastLevelFolderPath,
                            music.Path, music.Extension, music.BitDepth, music.BitRate, music.SampleRate, music.Channel,
                            music.TrackNumber, music.DiskNumber, music.Year, music.CreateTime, music.UpdateTime, music.CodecId, music.EncodingKind, music.Id);
                        if (music.AdditionalMetadataRead)
                            MusicMetadataStorage.Save(db, music.Id, AdditionalMusicMetadata.From(music));
                        if (existing?.IsDetached == true) added.Add(db.Get<Music>(music.Id));
                        else updated += changed;
                    }
                    if (embedded.TryGetValue(music, out var document))
                        WinUIMusicPlayer.Services.Lyrics.LyricsRepository.AddEmbeddedInTransaction(db, music.Id, document);
                }
            });
            if (added.Count > 0 || updated > 0) onCommitted?.Invoke();
            if (added.Count > 0 || updated > 0 || onCommitted is null)
            {
                int merged = await ApplyAutomaticSongLinksAsync(added);
                if (merged > 0) onCommitted?.Invoke();
            }
            if (added.Count > 0 && onBatchInserted is not null)
                await onBatchInserted(added);
            return (added, updated);
        }

        private Task<List<Music>> GetSongsInFolderAsync(string folderPath, bool recursive)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            if (!recursive)
                return _dbConnection.QueryAsync<Music>("SELECT * FROM Music WHERE FolderPath = ? COLLATE NOCASE", root);
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return _dbConnection.QueryAsync<Music>(
                "SELECT * FROM Music WHERE FolderPath = ? COLLATE NOCASE OR substr(FolderPath,1,length(?)) = ? COLLATE NOCASE",
                root, prefix, prefix);
        }

        /// <summary>扫描根下现有歌曲行数（含子目录），与 GetSongsInFolderAsync 的递归口径一致；供重扫进度做分母，不物化行。</summary>
        public Task<int> CountSongsInFolderAsync(string folderPath)
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folderPath));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            return _dbConnection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM Music WHERE FolderPath = ? COLLATE NOCASE OR substr(FolderPath,1,length(?)) = ? COLLATE NOCASE",
                root, prefix, prefix);
        }

        internal event Action? LocalFoldersChanged;

        public async Task RemoveFolder(int folderId)
        {
            var folder = await GetFolder(folderId);
            if (folder is null) return;
            if (folder.IsExternalImport)
            {
                // 虚拟行无磁盘路径可按包含删除：改按归属移除全部导入行（含离线盘，用户显式移除）
                // 与虚拟行本身；下次外部导入时自动重建。
                await RemoveExternalImportsCoreAsync(folder);
                return;
            }
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Path));
            string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
            const string inFolder = "SourceId=0 AND IsDetached=0 AND (FolderPath=? COLLATE NOCASE OR substr(FolderPath,1,length(?))=? COLLATE NOCASE)";
            await _dbConnection.RunInTransactionAsync(db =>
            {
                if (MusicMetadataStorage.HasSongs(db))
                    db.Execute("UPDATE Music SET IsDetached=1,IsMissing=1 WHERE " + inFolder, root, prefix, prefix);
                else
                    foreach (int id in db.QueryScalars<int>("SELECT Id FROM Music WHERE " + inFolder, root, prefix, prefix))
                    {
                        db.Delete<Music>(id);
                        WinUIMusicPlayer.Services.Lyrics.LyricsRepository.DeleteInTransaction(db, id);
                    }
                db.Execute("DELETE FROM SubFolder WHERE FolderId = ?", folderId);
                db.Delete<Folder>(folderId);
            });
            LocalFoldersChanged?.Invoke();
        }

        public async Task<Folder> GetFolder(int folderId)
        {
            return await _dbConnection.Table<Folder>().Where(f => f.Id == folderId).FirstOrDefaultAsync();
        }

        public async Task CheckFolderBeforeAdd(StorageFolder folder,
            Func<Folder, Task>? onFolderInserted = null,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted = null)
        {
            var existingFolders = await _dbConnection.Table<Folder>().ToListAsync();

            // 外部导入虚拟行是哨兵路径，不参与真实目录的重叠判定。
            bool folderAlreadyExists = existingFolders.Any(f =>
                !f.IsExternalImport
                && (LibraryPath.IsWithin(folder.Path, f.Path) || LibraryPath.IsWithin(f.Path, folder.Path)));

            if (!folderAlreadyExists)
            {
                var newFolder = new Folder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Type = Folder.TypeLocal
                };
                await _dbConnection.InsertAsync(newFolder);
                LocalFoldersChanged?.Invoke();
                // Folder 行先落库即回调：上层可让文件夹行先出现在列表中，SongCount 随扫描批次递增
                if (onFolderInserted is not null)
                    await onFolderInserted(newFolder);
                await ScanFolderAsync(folder, newFolder.Id, onBatchInserted);
            }
        }

        public async Task RescanFolder(int folderId, Func<IReadOnlyList<Music>, Task>? onBatchInserted = null,
            Action<int>? onFilesChecked = null)
        {
            var folder = await GetFolder(folderId);
            if (folder is null) return;
            if (folder.IsExternalImport)
            {
                // 虚拟行"重扫" = 存在性对账：只删确认缺失行，无新增可发布（刷新由调用方统一触发）。
                await ReconcileExternalImportsAsync();
                return;
            }
            await RescanFolderCoreAsync(folder.Path, true, false, onBatchInserted, onFilesChecked: onFilesChecked);
        }

        public async Task RescanFolderByPath(string folderPath, bool isUpdate = true, bool isSingleFolder = false,
            CancellationToken cancellationToken = default)
        {
            using var lease = await LibraryOperationGate.EnterAsync(cancellationToken);
            var folders = await GetFolders();
            bool managed = folders.Any(folder => !folder.IsExternalImport && LibraryPath.IsWithin(folderPath, folder.Path));
            if (!managed && !folders.Any(folder => folder.IsExternalImport)) return;
            bool committed = false;
            try
            {
                await RescanFolderCoreAsync(folderPath, true, isSingleFolder, cancellationToken: cancellationToken,
                    onCommitted: () => committed = true, attachedFilesOnly: !managed);
            }
            finally
            {
                if (isUpdate && committed) await PublishLibrarySnapshotAsync(default, preserveRows: true);
            }
        }

        private async Task<int> RescanFolderCoreAsync(string folderPath, bool updateExisting, bool singleFolder,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted = null, bool onlyChanged = false,
            CancellationToken cancellationToken = default, IReadOnlyList<string>? scannedPaths = null,
            Action<int>? onFilesChecked = null, Action? onCommitted = null, string? previousFolderPath = null,
            bool attachedFilesOnly = false)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await GetSongsInFolderAsync(folderPath, !singleFolder);
            if (previousFolderPath is not null && previousFolderPath != folderPath)
                existing.AddRange(await GetSongsInFolderAsync(previousFolderPath, !singleFolder));
            // Outside a registered root only explicitly imported files belong to the library.
            if (attachedFilesOnly)
            {
                existing.RemoveAll(song => song.IsRemote || song.IsDetached);
                if (existing.Count == 0) return 0;
                scannedPaths = existing.Where(song => File.Exists(song.Path)).Select(song => song.Path).ToList();
            }
            var remaining = new Dictionary<string, Music>(existing.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var song in existing)
                if (!song.IsRemote) remaining.TryAdd(song.Path, song);
            int changes = 0;

            // Only the source enumerator owns remaining; deletion reads it after every worker/consumer completes.
            IEnumerable<(string Path, Music? Existing)> EnumerateWork()
            {
                foreach (var path in scannedPaths ?? AddFolderService.EnumerateMusicPaths(folderPath, !singleFolder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining.Remove(path, out var old);
                    if (old is null || old.IsDetached || old.IsMissing || (updateExisting && (!onlyChanged || old.Path != path || File.GetLastWriteTime(path) != old.UpdateTime)))
                        yield return (path, old);
                    else onFilesChecked?.Invoke(1);
                }
            }

            await ScanPipeline.RunAsync(EnumerateWork(), async (work, token) =>
            {
                var result = await AddFolderService.ReadMusicAsync(work.Path, token);
                if (result.Music is not null && work.Existing is not null)
                    result.Music.Id = work.Existing.Id;
                return result;
            }, async batch =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var committed = await CommitScanBatchAsync(batch, onBatchInserted, onCommitted);
                changes += committed.Added.Count + committed.Updated;
                onFilesChecked?.Invoke(batch.Count);
            }, onBatchInserted is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500), cancellationToken);

            // An inaccessible/disconnected directory is not deletion evidence. Check again if
            // the watcher is reconciling a directory that may have vanished during enumeration.
            if (onCommitted is not null && !Directory.Exists(folderPath)) return changes;
            var missing = new List<Music>();
            foreach (var music in remaining.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!music.IsMissing && !music.IsDetached && LibraryPath.IsConfirmedMissing(music.Path, folderPath)) missing.Add(music);
            }
            if (missing.Count > 0)
            {
                int removed = await DeleteMusicBatchAsync(missing, cancellationToken);
                if (removed > 0) onCommitted?.Invoke();
                changes += removed;
            }
            return changes;
        }

        internal Task<int> ScanChangedFolderAsync(string folderPath, CancellationToken cancellationToken = default,
            IReadOnlyList<string>? scannedPaths = null, Action<int>? onFilesChecked = null)
            => RescanFolderCoreAsync(folderPath, true, false, onlyChanged: true, cancellationToken: cancellationToken,
                scannedPaths: scannedPaths, onFilesChecked: onFilesChecked);

        public Task<int> RescanFolderWithOutUpdateAll(string folderPath, bool isSingleFolder = false)
            => RescanFolderCoreAsync(folderPath, false, isSingleFolder);

        internal async Task ReconcileWatchedFolderAsync(string root, WatchedFolderChanges? changes, Action onCommitted,
            CancellationToken token)
        {
            if ((File.GetAttributes(root) & FileAttributes.Directory) == 0) return;
            if (changes is null)
            {
                await RescanFolderCoreAsync(root, true, false, onlyChanged: true, cancellationToken: token, onCommitted: onCommitted);
                return;
            }

            var subtrees = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var missingTrees = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (original, current) in changes.Directories)
            {
                if (!TryReadAttributes(current, out var attributes)) continue;
                if (attributes is null)
                {
                    missingTrees.Add(original);
                    missingTrees.Add(current);
                }
                else if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory)
                    subtrees.Add(original, current);
            }

            var parents = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var missingFiles = new List<string>();
            foreach (string path in changes.Files)
            {
                if (!TryReadAttributes(path, out var attributes)) continue;
                if (attributes is null) missingFiles.Add(path);
                else if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0 &&
                    Path.GetDirectoryName(path) is { } parent) parents.Add(parent);
            }

            foreach (var (original, current) in subtrees)
            {
                if (subtrees.Keys.Any(parent => parent != original && LibraryPath.IsWithin(original, parent))) continue;
                await ScanDirectoryAsync(current, singleFolder: false, previous: original);
            }
            foreach (string parent in parents)
            {
                if (!subtrees.Keys.Any(tree => LibraryPath.IsWithin(parent, tree)))
                    await ScanDirectoryAsync(parent, singleFolder: true);
            }

            if (missingFiles.Count > 0)
            {
                // FileName events can use IX_Music_Path_NoCase for the entire batch.
                var parameters = string.Join(',', Enumerable.Repeat("?", missingFiles.Count));
                var rows = await _dbConnection.QueryAsync<Music>(
                    $"SELECT Id,Path FROM Music WHERE SourceId=0 AND IsMissing=0 AND IsDetached=0 AND Path COLLATE NOCASE IN ({parameters})",
                    missingFiles.ToArray());
                await MarkMissingAsync(rows);
            }
            foreach (string path in missingTrees)
            {
                token.ThrowIfCancellationRequested();
                string prefix = Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
                var rows = await _dbConnection.QueryAsync<Music>(
                    "SELECT Id,Path FROM Music WHERE SourceId=0 AND IsMissing=0 AND IsDetached=0 AND " +
                    "(FolderPath=? COLLATE NOCASE OR substr(FolderPath,1,length(?))=? COLLATE NOCASE)", path, prefix, prefix);
                await MarkMissingAsync(rows);
            }

            bool TryReadAttributes(string path, out FileAttributes? attributes)
            {
                token.ThrowIfCancellationRequested();
                attributes = null;
                try { attributes = File.GetAttributes(path); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "无法检查监视路径 {Path}", path);
                    return false;
                }
                return true;
            }

            async Task MarkMissingAsync(List<Music> rows)
            {
                if (rows.Count == 0 || !Directory.Exists(root)) return;
                var confirmed = new List<Music>(rows.Count);
                foreach (var row in rows)
                {
                    token.ThrowIfCancellationRequested();
                    try { if (LibraryPath.IsConfirmedMissing(row.Path, root)) confirmed.Add(row); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "无法核对监视路径 {Path} 是否缺失", row.Path);
                    }
                }
                if (confirmed.Count > 0 && Directory.Exists(root)) await DeletedMusicList(confirmed, token, onCommitted);
            }

            async Task ScanDirectoryAsync(string path, bool singleFolder, string? previous = null)
            {
                try
                {
                    for (string? parent = path; parent is not null &&
                        !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase) && LibraryPath.IsWithin(parent, root);
                        parent = Path.GetDirectoryName(parent))
                    {
                        if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) return;
                    }
                    await RescanFolderCoreAsync(path, true, singleFolder, onlyChanged: true, cancellationToken: token,
                        onCommitted: onCommitted, previousFolderPath: previous);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "无法扫描监视目录 {Path}", path);
                }
            }
        }

        /// <summary>编码成功后以同目录重命名替换目标。在同一维护锁内写入曲库，迟到的监听通知只会核对已提交的版本。</summary>
        internal async Task InstallConvertedFileAsync(string temporaryPath, string outputPath, Action? onCommitted)
        {
            using var lease = await LibraryOperationGate.EnterAsync(reportBusy: false).ConfigureAwait(false);
            await _metadataGate.WaitAsync().ConfigureAwait(false);
            try
            {
                // Queued tags identify the old file by size and timestamp. Replacing it would strand that edit.
                if (await _dbConnection.ExecuteScalarAsync<int>("SELECT EXISTS(SELECT 1 FROM PendingMetadataWrite WHERE Path=? COLLATE NOCASE)", outputPath).ConfigureAwait(false) != 0)
                    throw new IOException($"转换目标有待写入的标签: {outputPath}");
                File.Move(temporaryPath, outputPath, overwrite: true);
                var result = await AddFolderService.ReadMusicAsync(outputPath, default).ConfigureAwait(false);
                if (result.Music is null) throw new IOException($"无法读取转换产物: {outputPath}");
                await CommitScanBatchAsync([result], null, onCommitted, replaceExistingPath: true).ConfigureAwait(false);
            }
            finally { _metadataGate.Release(); }
        }

        public Task AddMusicList(IEnumerable<Music> songs)
            => addFolderService.ScanPathsAsync(songs.Select(m => m.Path), batch => CommitScanBatchAsync(batch, null), TimeSpan.Zero);

        public Task UpdateMusicList(IEnumerable<Music> songs)
            => ScanPipeline.RunAsync(songs, async (old, token) =>
            {
                var result = await AddFolderService.ReadMusicAsync(old.Path, token);
                if (result.Music is not null) result.Music.Id = old.Id;
                return result;
            }, batch => CommitScanBatchAsync(batch, null), TimeSpan.Zero);

        public async Task DeletedMusicList(IEnumerable<Music> songs, CancellationToken cancellationToken = default,
            Action? onCommitted = null)
        {
            foreach (var batch in songs.Chunk(ScanPipeline.BatchSize))
            {
                if (await DeleteMusicBatchAsync(batch, cancellationToken) > 0) onCommitted?.Invoke();
            }
        }

        private async Task<int> DeleteMusicBatchAsync(IEnumerable<Music> songs, CancellationToken token)
        {
            int changed = 0;
            await _dbConnection.RunInTransactionAsync(db =>
            {
                token.ThrowIfCancellationRequested();
                bool keepVersions = MusicMetadataStorage.HasSongs(db);
                foreach (var music in songs)
                {
                    if (keepVersions)
                        changed += db.Execute("UPDATE Music SET IsMissing=1 WHERE Id=? AND IsMissing=0", music.Id);
                    else if (db.Delete<Music>(music.Id) > 0)
                    {
                        changed++;
                        WinUIMusicPlayer.Services.Lyrics.LyricsRepository.DeleteInTransaction(db, music.Id);
                    }
                }
            });
            return changed;
        }

        /// <summary>
        /// 外部导入行集合：路径不在任何本地扫描根内的 Music 行。归属始终按路径现算——
        /// 导入文件所在目录之后被加入扫描即自动移交为普通库内行，移除扫描根时随路径删除。
        /// </summary>
        private async Task<List<Music>> GetExternalImportRowsAsync(CancellationToken cancellationToken)
        {
            var localRoots = new List<string>();
            foreach (var folder in await GetFolders())
            {
                if (!folder.IsExternalImport && !string.IsNullOrEmpty(folder.Path))
                    localRoots.Add(folder.Path);
            }
            var owned = new List<Music>();
            foreach (var music in await _dbConnection.QueryAsync<Music>("SELECT Id,Path,IsMissing FROM Music WHERE SourceId=0 AND IsDetached=0"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool within = false;
                foreach (var root in localRoots)
                    if (LibraryPath.IsWithin(music.Path, root)) { within = true; break; }
                if (!within) owned.Add(music);
            }
            return owned;
        }

        /// <summary>
        /// 外部导入对账：删除磁盘上确认缺失的导入行（含歌词）。按盘根分组防御性判断——
        /// 盘根不可达（VHD/subst 卸载、盘符消失等）整组保留，与扫描根"枚举失败不推断删除"
        /// 的语义一致；单行意外 IO/权限错误同样保留。返回是否存在删除。
        /// </summary>
        public async Task<bool> ReconcileExternalImportsAsync(CancellationToken cancellationToken = default)
        {
            var owned = await GetExternalImportRowsAsync(cancellationToken);
            if (owned.Count == 0) return false;
            var missing = new List<Music>();
            var restored = new List<int>();
            foreach (var group in owned.GroupBy(
                         music => Path.GetPathRoot(music.Path) ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsDriveRootReachable(group.Key)) continue;
                foreach (var music in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (LibraryPath.IsConfirmedMissing(music.Path, Path.GetDirectoryName(music.Path) ?? music.Path) && !music.IsMissing)
                            missing.Add(music);
                        else if (music.IsMissing && File.Exists(music.Path)) restored.Add(music.Id);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 不可访问不构成删除证据，保留该行。
                    }
                }
            }
            if (restored.Count > 0)
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (int id in restored) db.Execute("UPDATE Music SET IsMissing=0 WHERE Id=? AND IsDetached=0", id);
                });
            if (missing.Count > 0) await DeletedMusicList(missing, cancellationToken);
            return missing.Count > 0 || restored.Count > 0;
        }

        /// <summary>盘根是否可达：DriveInfo 为元数据查询；IsReady=false 或根访问异常按不可达保留整组。</summary>
        private static bool IsDriveRootReachable(string root)
        {
            if (string.IsNullOrEmpty(root)) return false;
            try
            {
                if (!new DriveInfo(root).IsReady) return false;
                _ = File.GetAttributes(root);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>移除外部导入虚拟文件夹：删除全部导入行（含离线盘，用户显式移除）与虚拟行本身。</summary>
        private async Task RemoveExternalImportsCoreAsync(Folder external)
        {
            var owned = await GetExternalImportRowsAsync(CancellationToken.None);
            await _dbConnection.RunInTransactionAsync(db =>
            {
                bool keepVersions = MusicMetadataStorage.HasSongs(db);
                foreach (var music in owned)
                {
                    if (keepVersions) db.Execute("UPDATE Music SET IsDetached=1,IsMissing=1 WHERE Id=?", music.Id);
                    else { db.Delete<Music>(music.Id); WinUIMusicPlayer.Services.Lyrics.LyricsRepository.DeleteInTransaction(db, music.Id); }
                }
                db.Delete<Folder>(external.Id);
            });
        }

}
