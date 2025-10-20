using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class InitialFileScan
    {
        public static async Task InitialScan()
        {
            List<Music> MusicsToUpdate = [];
            List<Music> MusicsToAdd = [];
            List<Music> MusicsToDelete = [];
            IEnumerable<Music> allSongsCache = await MusicDatabaseService.GetMusicListAsync();
            HashSet<string> allScannedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var folderList = await MusicDatabaseService.GetFolders();
            Stopwatch totalSw = Stopwatch.StartNew();

            foreach (var folder in folderList)
            {
                if (!string.IsNullOrEmpty(folder.Path))
                {
                    await ScanSingleFolder(folder.Path, allScannedFilePaths, MusicsToUpdate, MusicsToAdd, allSongsCache);
                }
            }

            foreach (Music knownMusic in allSongsCache)
            {
                if (!allScannedFilePaths.Contains(knownMusic.Path))
                {
                    MusicsToDelete.Add(knownMusic);
                }
            }

            totalSw.Stop();
            Debug.WriteLine($"[Total Scan] Finished in: {totalSw.ElapsedMilliseconds} ms");
            Debug.WriteLine($"Scan Complete. Add: {MusicsToAdd.Count}, Update: {MusicsToUpdate.Count}, Delete: {MusicsToDelete.Count}");
            await MusicDatabaseService.AddMusicList(MusicsToAdd);
            await MusicDatabaseService.UpdateMusicList(MusicsToUpdate);
            await MusicDatabaseService.DeletedMusicList(MusicsToDelete);
            await Deduplication();            
        }

        public static async Task Deduplication()
        {
            var allSongs = await MusicDatabaseService.GetMusicListAsync();
            IEnumerable<Music> songsToDelete = allSongs.AsValueEnumerable().GroupBy(song => song.Path)
                        .Where(group => group.Count() > 1)
                        .SelectMany(group => group.Skip(1))
                        .ToList();
            await MusicDatabaseService.DeletedMusicList(songsToDelete);
        }

        // 将原 GetFileModificationDates 重命名，并接收 allScannedFilePaths
        public static async Task ScanSingleFolder(string rootDirectory, HashSet<string> allScannedFilePaths,List<Music> MusicsToUpdate, List<Music> MusicsToAdd,IEnumerable<Music> allSongsCache)
        {
            if (!Directory.Exists(rootDirectory))
            {
                Debug.WriteLine($"Directory not found: {rootDirectory}");
                return;
            }

            try
            {
                // 在 Task.Run 内部处理文件 I/O
                await Task.Run(() =>
                {
                    Stopwatch sw = Stopwatch.StartNew();

                    IEnumerable<string> filePaths = Directory.EnumerateFiles(
                        rootDirectory,
                        "*.*",
                        SearchOption.AllDirectories
                    );

                    // 遍历文件系统
                    foreach (string filePath in filePaths)
                    {
                        try
                        {
                            if (!ToolUtils.IsMusicFile(System.IO.Path.GetExtension(filePath)))
                            {
                                continue;
                            }

                            // **关键：将路径添加到全局 HashSet 中**
                            lock (allScannedFilePaths) // 保护对 HashSet 的写入，因为 Task.Run 可能并行执行
                            {
                                allScannedFilePaths.Add(filePath);
                            }

                            FileInfo fileInfo = new FileInfo(filePath);
                            DateTime lastModifiedDate = fileInfo.LastWriteTime;

                            Music music = allSongsCache.FirstOrDefault(m => m.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));

                            if (music != null)
                            {
                                if (music.UpdateTime != lastModifiedDate)
                                {
                                    lock (MusicsToUpdate) // 保护对结果列表的写入
                                    {
                                        MusicsToUpdate.Add(music);
                                    }
                                }
                            }
                            else
                            {
                                lock (MusicsToAdd) // 保护对结果列表的写入
                                {
                                    MusicsToAdd.Add(new Music { Path = filePath, UpdateTime = lastModifiedDate });
                                }
                            }
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            Debug.WriteLine($"Access Denied for file: {filePath}. Error: {ex.Message}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Error processing file: {filePath}. Error: {ex.Message}");
                        }
                    }

                    sw.Stop();
                    Debug.WriteLine($"[Scan Folder] {rootDirectory} finished in: {sw.ElapsedMilliseconds} ms");

                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"An unexpected error occurred in ScanSingleFolder: {ex.Message}");
            }
        }
    }
}
