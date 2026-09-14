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

namespace WinUIMusicPlayer.Services
{
    public class AddFolderService
    {
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
                string extension = GetExtensionUpper(filePath);

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
                        Extension = GetExtensionUpper(filePath),
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

        private static string GetExtensionUpper(string filePath)
        {
            ReadOnlySpan<char> s = filePath;
            int dot = s.LastIndexOf('.');
            return dot >= 0 ? new string(s[(dot + 1)..]).ToUpperInvariant() : "";
        }


        public const int ScanBatchSize = ScanPipeline.BatchSize;

        public Task GetMusicFilesRecursiveBatched(StorageFolder folder,
            Func<IReadOnlyList<(Music Music, string Lyrics)>, Task> onBatch)
            => ScanPathsAsync(EnumerateMusicPaths(folder.Path), onBatch);

        internal Task ScanPathsAsync(IEnumerable<string> paths,
            Func<IReadOnlyList<(Music Music, string Lyrics)>, Task> onBatch, TimeSpan? interval = null)
            => ScanPipeline.RunAsync(paths, ReadMusicAsync, onBatch, interval ?? TimeSpan.FromMilliseconds(500));

        internal static IEnumerable<string> EnumerateMusicPaths(string root, bool recursive = true)
        {
            // Stream file names. Skip junctions/symlinks to prevent cycles and duplicate traversal.
            // Do not suppress access errors: a partial rescan must never infer deletions.
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = recursive,
                IgnoreInaccessible = false,
                AttributesToSkip = System.IO.FileAttributes.ReparsePoint
            };
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
                if (ToolUtils.IsMusicFile(Path.GetExtension(path)))
                    yield return path;
        }

        internal static async ValueTask<(Music Music, string Lyrics)> ReadMusicAsync(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (AudioFileWriteGate.IsBeingWritten(path)) return (null!, "");
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                var (music, lyrics) = await ToolUtils.GetMusicInfo(file).ConfigureAwait(false);
                return (music!, lyrics ?? "");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "读取扫描文件失败: {Path}", path);
                return (null!, "");
            }
        }
    }
}
