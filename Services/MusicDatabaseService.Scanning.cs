using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.ViewModel;

namespace WinUIMusicPlayer.Services;

public partial class MusicDatabaseService
{
        private sealed class DirectorySongCount
        {
            public string Path { get; set; } = "";
            public int SongCount { get; set; }
        }

        public async Task<List<Folder>> GetFoldersWithSongCountsAsync()
        {
            var folders = await GetFolders();
            // The persisted Folder.SongCount can be stale in older databases. Count actual songs,
            // grouping once by directory rather than materializing every Music or relying on UI startup order.
            var counts = await _dbConnection.QueryAsync<DirectorySongCount>(
                "SELECT FolderPath AS Path, COUNT(*) AS SongCount FROM Music GROUP BY FolderPath COLLATE NOCASE");
            foreach (var folder in folders)
            {
                folder.SongCount = 0;
                foreach (var count in counts)
                    if (LibraryPath.IsWithin(count.Path, folder.Path)) folder.SongCount += count.SongCount;
            }
            return folders;
        }

        public async Task ScanFolderAsync(StorageFolder folder, int folderId,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted = null)
        {
            // Read paths only, not every song's metadata/strings. This snapshot is immutable while workers run.
            var existingPaths = new HashSet<string>(
                await _dbConnection.QueryScalarsAsync<string>("SELECT Path FROM Music"), StringComparer.OrdinalIgnoreCase);
            await addFolderService.ScanPathsAsync(
                AddFolderService.EnumerateMusicPaths(folder.Path).Where(path => !existingPaths.Contains(path)),
                batch => CommitScanBatchAsync(batch, onBatchInserted));
            // Do not walk the entire tree before delivering the first song.
            await InsertSubFolders(AutoRescanService.RecordInitialFolderTimes(folder.Path, folderId));
        }

        /// <summary>提交一批扫描结果；返回新增行（供增量 UI 回调复用）与 UPDATE 命中的行数——
        /// 两者都属于数据库变更，调用方据此决定是否刷新视图。</summary>
        private async Task<(List<Music> Added, int Updated)> CommitScanBatchAsync(IReadOnlyList<(Music Music, string Lyrics)> batch,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted)
        {
            var added = new List<Music>(batch.Count);
            int updated = 0;
            await _dbConnection.RunInTransactionAsync(db =>
            {
                foreach (var (music, lyrics) in batch)
                {
                    if (music is null) continue;
                    // 延迟写入期间文件仍是旧标签，不能覆盖用户已保存的编辑。
                    if (db.ExecuteScalar<int>("SELECT COUNT(*) FROM PendingMetadataWrite WHERE Path = ? COLLATE NOCASE", music.Path) != 0)
                        continue;
                    if (music.Id == 0)
                    {
                        // Recheck under the transaction: conversions can publish files independently.
                        if (db.ExecuteScalar<int>("SELECT Id FROM Music WHERE Path = ? COLLATE NOCASE LIMIT 1", music.Path) != 0)
                            continue;
                        db.Insert(music);
                        added.Add(music);
                    }
                    else
                    {
                        // Update only metadata; favorite/order/play count/lyrics offsets may change during a scan.
                        updated += db.Execute("UPDATE Music SET Title=?, Author=?, Album=?, Duration=?, FolderPath=?, LastLevelFolderPath=?, " +
                            "Extension=?, BitDepth=?, BitRate=?, SampleRate=?, Channel=?, TrackNumber=?, DiskNumber=?, Year=?, " +
                            "CreateTime=?, UpdateTime=? WHERE Id=?",
                            music.Title, music.Author, music.Album, music.Duration, music.FolderPath, music.LastLevelFolderPath,
                            music.Extension, music.BitDepth, music.BitRate, music.SampleRate, music.Channel,
                            music.TrackNumber, music.DiskNumber, music.Year, music.CreateTime, music.UpdateTime, music.Id);
                    }
                    if (string.IsNullOrWhiteSpace(lyrics)) continue;
                    var existing = db.Find<MusicLyrics>(music.Id);
                    if (existing is null || (string.IsNullOrWhiteSpace(existing.Lyrics)
                        && string.IsNullOrWhiteSpace(existing.TranslatedLyrics)
                        && string.IsNullOrWhiteSpace(existing.Krc) && string.IsNullOrWhiteSpace(existing.TKrc)))
                        db.InsertOrReplace(new MusicLyrics { MusicId = music.Id, Lyrics = lyrics });
                }
            });
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
                "SELECT * FROM Music WHERE FolderPath = ? COLLATE NOCASE OR substr(FolderPath,1,?) = ? COLLATE NOCASE",
                root, prefix.Length, prefix);
        }

        public async Task RemoveFolder(int folderId)
        {
            var folder = await GetFolder(folderId);
            if (folder is null) return;
            var songs = await GetSongsInFolderAsync(folder.Path, true);
            await _dbConnection.RunInTransactionAsync(db =>
            {
                foreach (var music in songs)
                {
                    db.Delete<Music>(music.Id);
                    db.Delete<MusicLyrics>(music.Id);
                }
                db.Execute("DELETE FROM SubFolder WHERE FolderId = ?", folderId);
                db.Delete<Folder>(folderId);
            });
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

            bool folderAlreadyExists = existingFolders.Any(f =>
                LibraryPath.IsWithin(folder.Path, f.Path) || LibraryPath.IsWithin(f.Path, folder.Path));

            if (!folderAlreadyExists)
            {
                var newFolder = new Folder
                {
                    Name = folder.Name,
                    Path = folder.Path,
                    Type = "本地"
                };
                await _dbConnection.InsertAsync(newFolder);
                // Folder 行先落库即回调：上层可让文件夹行先出现在列表中，SongCount 随扫描批次递增
                if (onFolderInserted is not null)
                    await onFolderInserted(newFolder);
                await ScanFolderAsync(folder, newFolder.Id, onBatchInserted);
            }
        }

        public async Task RescanFolder(int folderId, Func<IReadOnlyList<Music>, Task>? onBatchInserted = null)
        {
            var folder = await GetFolder(folderId);
            if (folder is not null)
                await RescanFolderCoreAsync(folder.Path, true, false, onBatchInserted);
        }

        public async Task RescanFolderByPath(string folderPath, bool isUpdate = true, bool isSingleFolder = false)
        {
            using var lease = LibraryOperationGate.TryEnter();
            if (lease is null) return;
            try { await RescanFolderCoreAsync(folderPath, true, isSingleFolder); }
            finally
            {
                if (isUpdate) await App.Services.GetRequiredService<AppViewModel>().RefreshSongsSourceAsync();
            }
        }

        private async Task<int> RescanFolderCoreAsync(string folderPath, bool updateExisting, bool singleFolder,
            Func<IReadOnlyList<Music>, Task>? onBatchInserted = null, bool onlyChanged = false,
            CancellationToken cancellationToken = default, IReadOnlyList<string>? scannedPaths = null,
            Action<int>? onFilesChecked = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await GetSongsInFolderAsync(folderPath, !singleFolder);
            var remaining = new Dictionary<string, Music>(existing.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var song in existing) remaining.TryAdd(song.Path, song);
            int changes = 0;

            // Only the source enumerator owns remaining; deletion reads it after every worker/consumer completes.
            IEnumerable<(string Path, Music? Existing)> EnumerateWork()
            {
                foreach (var path in scannedPaths ?? AddFolderService.EnumerateMusicPaths(folderPath, !singleFolder))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    remaining.Remove(path, out var old);
                    if (old is null || (updateExisting && (!onlyChanged || File.GetLastWriteTime(path) != old.UpdateTime)))
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
                var committed = await CommitScanBatchAsync(batch, onBatchInserted);
                changes += committed.Added.Count + committed.Updated;
                onFilesChecked?.Invoke(batch.Count);
            }, onBatchInserted is null ? TimeSpan.Zero : TimeSpan.FromMilliseconds(500), cancellationToken);

            // An inaccessible/disconnected directory throws before this point. Keep its database rows intact.
            var missing = new List<Music>();
            foreach (var music in remaining.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (LibraryPath.IsConfirmedMissing(music.Path, folderPath)) missing.Add(music);
            }
            if (missing.Count > 0)
            {
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var music in missing)
                    {
                        db.Delete<Music>(music.Id);
                        db.Delete<MusicLyrics>(music.Id);
                    }
                });
                changes += missing.Count;
            }
            return changes;
        }

        internal Task<int> ScanChangedFolderAsync(string folderPath, CancellationToken cancellationToken = default,
            IReadOnlyList<string>? scannedPaths = null, Action<int>? onFilesChecked = null)
            => RescanFolderCoreAsync(folderPath, true, false, onlyChanged: true, cancellationToken: cancellationToken,
                scannedPaths: scannedPaths, onFilesChecked: onFilesChecked);

        // Called by AutoScan while its library-operation lease is held. The count includes updates and deletions.
        public Task<int> RescanFolderWithOutUpdateAll(string folderPath, bool isSingleFolder = false)
            => RescanFolderCoreAsync(folderPath, false, isSingleFolder);

        public Task AddMusicList(IEnumerable<Music> songs)
            => addFolderService.ScanPathsAsync(songs.Select(m => m.Path), batch => CommitScanBatchAsync(batch, null), TimeSpan.Zero);

        public Task UpdateMusicList(IEnumerable<Music> songs)
            => ScanPipeline.RunAsync(songs, async (old, token) =>
            {
                var result = await AddFolderService.ReadMusicAsync(old.Path, token);
                if (result.Music is not null) result.Music.Id = old.Id;
                return result;
            }, batch => CommitScanBatchAsync(batch, null), TimeSpan.Zero);

        public async Task DeletedMusicList(IEnumerable<Music> songs, CancellationToken cancellationToken = default)
        {
            foreach (var batch in songs.Chunk(ScanPipeline.BatchSize))
                await _dbConnection.RunInTransactionAsync(db =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var music in batch)
                    {
                        db.Delete<Music>(music.Id);
                        db.Delete<MusicLyrics>(music.Id);
                    }
                });
        }

}
