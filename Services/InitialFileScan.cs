using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class InitialFileScan
    {
        private static ILogger<InitialFileScan> _logger = App.GetLogger<InitialFileScan>();

        public static async Task InitialScan()
        {
            List<Music> MusicsToUpdate = [];
            List<Music> MusicsToAdd = [];
            List<Music> MusicsToDelete = [];
            IEnumerable<Music> allSongsCache = await App.Services.GetRequiredService<MusicDatabaseService>().GetMusicListAsync();
            HashSet<string> allScannedFilePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var folderList = await App.Services.GetRequiredService<MusicDatabaseService>().GetFolders();
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
            await App.Services.GetRequiredService<MusicDatabaseService>().AddMusicList(MusicsToAdd);
            await App.Services.GetRequiredService<MusicDatabaseService>().UpdateMusicList(MusicsToUpdate);
            await App.Services.GetRequiredService<MusicDatabaseService>().DeletedMusicList(MusicsToDelete);
            await Deduplication();
        }

        public static async Task Deduplication()
        {
            var allSongs = await App.Services.GetRequiredService<MusicDatabaseService>().GetMusicListAsync();
            IEnumerable<Music> songsToDelete = allSongs.AsValueEnumerable().GroupBy(song => song.Path)
                        .Where(group => group.AsValueEnumerable().Count() > 1)
                        .SelectMany(group => group.AsValueEnumerable().Skip(1))
                        .ToList();
            await App.Services.GetRequiredService<MusicDatabaseService>().DeletedMusicList(songsToDelete);
        }

        // 将原 GetFileModificationDates 重命名，并接收 allScannedFilePaths
        public static async Task ScanSingleFolder(string rootDirectory, HashSet<string> allScannedFilePaths, List<Music> MusicsToUpdate, List<Music> MusicsToAdd, IEnumerable<Music> allSongsCache)
        {
            if (!Directory.Exists(rootDirectory))
            {
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

                            Music music = allSongsCache.AsValueEnumerable().FirstOrDefault(m => m.Path.Equals(filePath, StringComparison.OrdinalIgnoreCase));

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
                            _logger.LogWarning(ex, $"ScanSingleFolder 访问被拒绝: {filePath}");
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, $"ScanSingleFolder 处理文件错误: {filePath}");
                        }
                    }

                    sw.Stop();

                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"ScanSingleFolder 意外错误: {ex.Message}");
            }
        }
    }
}
