using ATL;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;
using ZLinq;

namespace WinUIMusicPlayer.Services
{
    public class AddFolderService
    {
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(8, 8);
        private static ILogger<AddFolderService> _logger = App.GetLogger<AddFolderService>();
        public AddFolderService()
        {
        }

        public UsbDeviceMusic GetUsbDeviceMusicInfo(StorageFile file, string folderPath, string uniqueDeviceId)
        {
            try
            {
                Track track = new(file.Path);
                string title = "未知标题";
                string artist = "未知艺术家";
                string album = "未知专辑";
                title = !string.IsNullOrWhiteSpace(track.Title) ?
                   track.Title : Path.GetFileNameWithoutExtension(file.Name);

                if (!string.IsNullOrWhiteSpace(track.Artist))
                {
                    artist = track.Artist;
                }
                if (!string.IsNullOrWhiteSpace(track.Album))
                {
                    album = track.Album;
                }
                var music = new UsbDeviceMusic
                {
                    Path = file.Path,
                    Title = title,
                    Author = artist,
                    Album = album,
                    Extension = file.FileType.TrimStart('.').ToUpper(),
                    UniqueDeviceId = uniqueDeviceId
                };
                return music;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetUsbDeviceMusicInfo 读取元数据失败: {file.Path}");
                try
                {
                    var music = new UsbDeviceMusic
                    {
                        Path = file.Path,
                        Title = Path.GetFileNameWithoutExtension(file.Name),
                        Author = "未知艺术家",
                        Album = "未知专辑",
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        UniqueDeviceId = uniqueDeviceId
                    };
                    return music;
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, $"GetUsbDeviceMusicInfo 创建基本音乐条目时出错: {file.Path}");
                }
            }
            return null;
        }

        public UsbDeviceMusic GetUsbDeviceMusicInfoByPath(string filePath, string folderPath, string uniqueDeviceId)
        {
            try
            {
                Track track = new(filePath);
                string title = !string.IsNullOrWhiteSpace(track.Title)
                    ? track.Title : Path.GetFileNameWithoutExtension(filePath);
                string artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist : "未知艺术家";
                string album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album : "未知专辑";
                string extension = Path.GetExtension(filePath).TrimStart('.').ToUpper();

                return new UsbDeviceMusic
                {
                    Path = filePath,
                    Title = title,
                    Author = artist,
                    Album = album,
                    Extension = extension,
                    UniqueDeviceId = uniqueDeviceId
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"GetUsbDeviceMusicInfoByPath 读取元数据失败: {filePath}");
                try
                {
                    return new UsbDeviceMusic
                    {
                        Path = filePath,
                        Title = Path.GetFileNameWithoutExtension(filePath),
                        Author = "未知艺术家",
                        Album = "未知专辑",
                        Extension = Path.GetExtension(filePath).TrimStart('.').ToUpper(),
                        UniqueDeviceId = uniqueDeviceId
                    };
                }
                catch (Exception innerEx)
                {
                    _logger.LogError(innerEx, $"GetUsbDeviceMusicInfoByPath 创建基本音乐条目时出错: {filePath}");
                }
            }
            return null;
        }


        public async Task GetMusicFilesRecursive(StorageFolder folder, List<Music> musicFiles)
        {
            DateTime startTime = DateTime.Now;
            var files = await folder.GetFilesAsync();
            // 筛选出音乐文件
            var musicFilesList = files.AsValueEnumerable().Where(file => ToolUtils.IsMusicFile(file.FileType)).ToList();
            // 并行处理音乐文件
            var tasks = musicFilesList.AsValueEnumerable().Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    Music music = await ToolUtils.GetMusicInfo(file);
                    return music;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"GetMusicFilesRecursive 处理文件出错: {file.Name}");
                    return null;
                }
                finally
                {
                    semaphore.Release(); // 释放信号量
                }
            });
            var results = await Task.WhenAll(tasks.ToList());
            lock (musicFiles)
            {
                foreach (var music in results.AsValueEnumerable().Where(m => m is not null))
                {
                    musicFiles.Add(music);
                }
            }
            // 递归扫描子文件夹 - 也可以并行处理
            var subfolders = await folder.GetFoldersAsync();
            var subfolderTasks = subfolders.AsValueEnumerable().Select(subfolder =>
                GetMusicFilesRecursive(subfolder, musicFiles));
            await Task.WhenAll(subfolderTasks.ToList());
        }
    }
}
