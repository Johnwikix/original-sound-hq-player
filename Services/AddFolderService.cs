using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TagLib;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services
{
    public class AddFolderService
    {
        private static readonly SemaphoreSlim semaphore = new SemaphoreSlim(8, 8);
        public AddFolderService()
        {
        }

        public UsbDeviceMusic getUsbDeviceMusicInfo(StorageFile file, string folderPath, string uniqueDeviceId)
        {
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(file.Path))
                {
                    Tag tag = audioFile.Tag;
                    string title = "未知标题";
                    string artist = "未知艺术家";
                    string album = "未知专辑";
                    title = !string.IsNullOrWhiteSpace(tag.Title) ?
                       tag.Title : Path.GetFileNameWithoutExtension(file.Name);

                    string[] artists = audioFile.Tag.Performers;
                    if (artists.Length > 0)
                    {
                        artist = artists[0]; // 取第一个艺术家
                        Console.WriteLine("艺术家: " + string.Join(", ", artists));
                    }
                    if (!string.IsNullOrWhiteSpace(tag.Album))
                    {
                        album = tag.Album;
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
            }
            catch (Exception)
            {
                // 即使提取元数据失败，也尝试添加基本信息
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
                    System.Diagnostics.Debug.WriteLine($"创建基本音乐条目时出错: {file.Path}, 错误: {innerEx.Message}");                     // 返回null以指示错误
                }
            }
            return null;
        }



        public async Task GetMusicFilesRecursive(StorageFolder folder, List<Music> musicFiles)
        {
            DateTime startTime = DateTime.Now;
            var files = await folder.GetFilesAsync();
            // 筛选出音乐文件
            var musicFilesList = files.Where(file => ToolUtils.IsMusicFile(file.FileType)).ToList();
            // 并行处理音乐文件
            var tasks = musicFilesList.Select(async file =>
            {
                await semaphore.WaitAsync();
                try
                {
                    Music music = await ToolUtils.GetMusicInfo(file, folder.Path);
                    return music;
                }
                catch (Exception ex)
                {
                    // 记录错误但不中断整个过程
                    System.Diagnostics.Debug.WriteLine($"处理文件 {file.Name} 时出错: {ex.Message}");
                    return null;
                }
                finally
                {
                    semaphore.Release(); // 释放信号量
                }
            });
            var results = await Task.WhenAll(tasks);
            // 添加有效结果到列表（线程安全）
            lock (musicFiles)
            {
                foreach (var music in results.Where(m => m is not null))
                {
                    musicFiles.Add(music);
                }
            }
            // 递归扫描子文件夹 - 也可以并行处理
            var subfolders = await folder.GetFoldersAsync();
            var subfolderTasks = subfolders.Select(subfolder =>
                GetMusicFilesRecursive(subfolder, musicFiles));
            await Task.WhenAll(subfolderTasks);
            Debug.WriteLine($"扫描文件夹 {folder.Path} 完成，耗时: {(DateTime.Now - startTime).TotalSeconds} 秒");
        }
    }
}
