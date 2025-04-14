using ATL;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services
{
    public class AddFolderService
    {
        public AddFolderService()
        {
        }
        public async Task<Music> getMusicInfo(StorageFile file, string folderPath)
        {
            try
            {
                Track theTrack = new Track(file.Path);
                int trackNumber = (int)theTrack.TrackNumber;
                string title = !string.IsNullOrWhiteSpace(theTrack.Title) ?
                   theTrack.Title : Path.GetFileNameWithoutExtension(file.Name);
                string artist = "未知艺术家";
                if (!string.IsNullOrWhiteSpace(theTrack.Artist))
                {
                    artist = theTrack.Artist;
                }

                string album = "未知专辑";
                if (!string.IsNullOrWhiteSpace(theTrack.Album))
                {
                    album = theTrack.Album;
                }

                string lastLevelFolderPath = Path.GetFileName(folderPath);

                int sampleRate =(int)theTrack.SampleRate;
                int channelCount = 0;
                int bitDepth = theTrack.BitDepth;
                int bitRate = theTrack.Bitrate;
                int year = (int)theTrack.Year;              

                var music = new Music
                {
                    Path = file.Path,
                    Title = title,
                    Author = artist,
                    Album = album,
                    Duration = TimeSpan.FromSeconds(theTrack.Duration),
                    FolderPath = folderPath,
                    Order = 0,
                    LastLevelFolderPath = lastLevelFolderPath,
                    Extension = file.FileType.TrimStart('.').ToUpper(),
                    BitDepth = bitDepth,
                    BitRate = bitRate,
                    SampleRate = sampleRate,
                    Channel = channelCount,
                    TrackNumber = trackNumber,
                    Year = year,
                    Lyrics = theTrack.Lyrics.ToString(),
                };
                return music;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"处理音乐文件时出错: {file.Path}, 错误: {ex.Message}");
                AudioFileInfo wavFileInfo = new AudioFileInfo();
                try
                {
                    wavFileInfo = ToolUtils.GetAudioFileInfo(file.Path);
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Debug.WriteLine($"获取WAV文件属性时出错: {exception.Message}");
                }
                // 即使提取元数据失败，也尝试添加基本信息
                try
                {
                    var music = new Music
                    {
                        Path = file.Path,
                        Title = Path.GetFileNameWithoutExtension(file.Name),
                        Author = "未知艺术家",
                        Duration = wavFileInfo.Duration,
                        Album = "未知专辑",
                        FolderPath = folderPath,
                        Order = 0,
                        LastLevelFolderPath = Path.GetFileName(folderPath),
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        BitDepth = wavFileInfo.BitDepth,
                        BitRate = wavFileInfo.BitRate,
                        SampleRate = wavFileInfo.SampleRate,
                        Year = 0,
                        Channel = wavFileInfo.ChannelCount                        
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
            var files = await folder.GetFilesAsync();
            foreach (StorageFile file in files)
            {
                if (ToolUtils.IsMusicFile(file.FileType))
                {
                    Music music = await getMusicInfo(file, folder.Path);
                    if (music != null)
                    {
                        musicFiles.Add(music);
                    }
                }
            }

            // 递归扫描子文件夹 - 保持不变
            var subfolders = await folder.GetFoldersAsync();
            foreach (var subfolder in subfolders)
            {
                await GetMusicFilesRecursive(subfolder, musicFiles);
            }
        }
    }
}
