using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TagLib;
using Windows.Storage;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Services
{
    public class AddFolderService
    {
        public AddFolderService()
        {
        }

        private async Task<int> GetBitDepth(StorageFile file)
        {
            var bitDepth = 16;
            try
            {
                // 其余代码保持不变...
                var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                                "System.Audio.SampleSize"
                             });
                // 处理位深度
                if (audioProps.ContainsKey("System.Audio.SampleSize") && audioProps["System.Audio.SampleSize"] != null)
                {
                    var sampleSize = Convert.ToInt32(audioProps["System.Audio.SampleSize"]);
                    bitDepth = sampleSize;
                }
            }
            catch (Exception ex)
            {
                // 处理异常
                bitDepth = 16;
                System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
            }
            return bitDepth;
        }

        private async Task<int> GetBitRate(StorageFile file)
        {
            var bitRate = 0;
            try
            {
                // 其余代码保持不变...
                var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                                "System.Audio.EncodingBitrate"
                             });
                if (audioProps.ContainsKey("System.Audio.EncodingBitrate") && audioProps["System.Audio.EncodingBitrate"] != null)
                {
                    int rawBitrate = Convert.ToInt32(audioProps["System.Audio.EncodingBitrate"]);
                    bitRate = rawBitrate > 0 ? rawBitrate / 1000 : 0;
                }
            }
            catch (Exception ex)
            {
                // 处理异常
                bitRate = 0;
                System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
            }
            return bitRate;
        }
        public async Task<Music> getMusicInfo(StorageFile file, string folderPath)
        {
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(file.Path))
                {
                    Tag tag = audioFile.Tag;
                    string title = "未知标题";
                    string artist = "未知艺术家";
                    string album = "未知专辑";
                    int trackNumber = 0;
                    int sampleRate = 0;
                    int bitDepth = 0;
                    int bitRate = 0;
                    int year = 0;
                    int channelCount = 0;
                    string lyrics = string.Empty;
                    TimeSpan duration = TimeSpan.Zero;
                    //string lastLevelFolderPath = Path.GetFileName(folderPath);
                    string lastLevelDirectory = Path.GetDirectoryName(file.Path);
                    DirectoryInfo directoryInfo = new DirectoryInfo(lastLevelDirectory);
                    string lastLevelFolderPath = directoryInfo.Name;
                    Properties audioProperties = audioFile.Properties;
                    trackNumber = (int)tag.Track;
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
                    sampleRate = audioProperties.AudioSampleRate;
                    bitDepth = audioProperties.BitsPerSample == 0 ? await GetBitDepth(file) : audioProperties.BitsPerSample;
                    bitRate = audioProperties.AudioBitrate == 0 ? await GetBitRate(file) : audioProperties.AudioBitrate;
                    year = (int)tag.Year;
                    duration = audioProperties.Duration;
                    channelCount = audioProperties.AudioChannels;
                    lyrics = tag.Lyrics;


                    var music = new Music
                    {
                        Path = file.Path,
                        Title = title,
                        Author = artist,
                        Album = album,
                        Duration = duration,
                        FolderPath = lastLevelDirectory,
                        Order = 0,
                        LastLevelFolderPath = lastLevelFolderPath,
                        Extension = file.FileType.TrimStart('.').ToUpper(),
                        BitDepth = bitDepth,
                        BitRate = bitRate,
                        SampleRate = sampleRate,
                        Channel = channelCount,
                        TrackNumber = trackNumber,
                        Year = year,
                        Lyrics = lyrics
                    };
                    return music;
                }
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

        public UsbDeviceMusic getUsbDeviceMusicInfo(StorageFile file, string folderPath,string uniqueDeviceId)
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
            catch (Exception ex)
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

            // 递归扫描子文件夹
            var subfolders = await folder.GetFoldersAsync();
            foreach (var subfolder in subfolders)
            {
                await GetMusicFilesRecursive(subfolder, musicFiles);
            }
        }
    }
}
