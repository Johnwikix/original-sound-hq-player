using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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
        public async Task<Music> getMusicInfo(StorageFile file, string folderPath)
        {
            try
            {
                // 获取音乐属性 - 使用多种方式提取元数据
                var musicProperties = await file.Properties.GetMusicPropertiesAsync();
                int trackNumber = (int)musicProperties.TrackNumber;

                // 标题处理 - 确保有有效标题
                string title = !string.IsNullOrWhiteSpace(musicProperties.Title) ?
                    musicProperties.Title : Path.GetFileNameWithoutExtension(file.Name);

                // 作者/艺术家处理 - 尝试额外的属性获取方式
                string artist = "未知艺术家";
                if (!string.IsNullOrWhiteSpace(musicProperties.Artist))
                {
                    artist = musicProperties.Artist;
                }
                else
                {
                    // 尝试通过额外的属性获取艺术家信息
                    var extraProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                            "System.Music.Artist",
                            "System.Author",
                            "System.Music.AlbumArtist"
                        });

                    if (extraProps.ContainsKey("System.Music.Artist") && extraProps["System.Music.Artist"] != null)
                    {
                        var propValue = extraProps["System.Music.Artist"];
                        if (propValue is string strValue && !string.IsNullOrWhiteSpace(strValue))
                            artist = strValue;
                        else if (propValue is IList<string> strList && strList.Count > 0)
                            artist = string.Join(", ", strList);
                    }
                    else if (extraProps.ContainsKey("System.Author") && extraProps["System.Author"] != null)
                    {
                        var propValue = extraProps["System.Author"];
                        if (propValue is string strValue && !string.IsNullOrWhiteSpace(strValue))
                            artist = strValue;
                        else if (propValue is IList<string> strList && strList.Count > 0)
                            artist = string.Join(", ", strList);
                    }
                    else if (extraProps.ContainsKey("System.Music.AlbumArtist") && extraProps["System.Music.AlbumArtist"] != null)
                    {
                        var propValue = extraProps["System.Music.AlbumArtist"];
                        if (propValue is string strValue && !string.IsNullOrWhiteSpace(strValue))
                            artist = strValue;
                        else if (propValue is IList<string> strList && strList.Count > 0)
                            artist = string.Join(", ", strList);
                    }
                }

                // 专辑处理 - 尝试额外的属性获取方式
                string album = "未知专辑";
                if (!string.IsNullOrWhiteSpace(musicProperties.Album))
                {
                    album = musicProperties.Album;
                }
                else
                {
                    // 尝试通过额外的属性获取专辑信息
                    var extraProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                            "System.Music.Album"
                        });

                    if (extraProps.ContainsKey("System.Music.Album") && extraProps["System.Music.Album"] != null
                        && extraProps["System.Music.Album"] is string albumStr && !string.IsNullOrWhiteSpace(albumStr))
                    {
                        album = albumStr;
                    }
                    // 如果获取不到专辑信息，保持为"未知专辑"
                }

                // 获取最后一级目录名
                string lastLevelFolderPath = Path.GetFileName(folderPath);

                // 保持原有的音频属性提取逻辑
                int sampleRate = 0;
                int channelCount = 0;
                int bitDepth = 0;
                int bitRate = 0;

                // 尝试获取详细的音频属性
                try
                {
                    // 其余代码保持不变...
                    var audioProps = await file.Properties.RetrievePropertiesAsync(new string[] {
                                "System.Audio.SampleRate",
                                "System.Audio.ChannelCount",
                                "System.Audio.EncodingBitrate",
                                "System.Audio.SampleSize"
                             });

                    // 处理采样率
                    if (audioProps.ContainsKey("System.Audio.SampleRate") && audioProps["System.Audio.SampleRate"] != null)
                    {
                        sampleRate = Convert.ToInt32(audioProps["System.Audio.SampleRate"]);
                    }

                    // 处理声道数
                    if (audioProps.ContainsKey("System.Audio.ChannelCount") && audioProps["System.Audio.ChannelCount"] != null)
                    {
                        channelCount = Convert.ToInt32(audioProps["System.Audio.ChannelCount"]);
                    }

                    // 处理比特率
                    if (audioProps.ContainsKey("System.Audio.EncodingBitrate") && audioProps["System.Audio.EncodingBitrate"] != null)
                    {
                        int rawBitrate = Convert.ToInt32(audioProps["System.Audio.EncodingBitrate"]);
                        bitRate = rawBitrate > 0 ? rawBitrate / 1000 : 0;
                    }

                    // 处理位深度
                    if (audioProps.ContainsKey("System.Audio.SampleSize") && audioProps["System.Audio.SampleSize"] != null)
                    {
                        var sampleSize = Convert.ToInt32(audioProps["System.Audio.SampleSize"]);
                        bitDepth = sampleSize;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"获取音频属性时出错: {ex.Message}");
                }

                var music = new Music
                {
                    Path = file.Path,
                    Title = title,
                    Author = artist,
                    Duration = musicProperties.Duration,
                    Album = album,
                    FolderPath = folderPath,
                    Order = 0,
                    LastLevelFolderPath = lastLevelFolderPath,
                    Extension = file.FileType.TrimStart('.').ToUpper(),
                    BitDepth = bitDepth,
                    BitRate = bitRate,
                    SampleRate = sampleRate,
                    Channel = channelCount,
                    TrackNumber = trackNumber,
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
                        Channel = wavFileInfo.ChannelCount,
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
