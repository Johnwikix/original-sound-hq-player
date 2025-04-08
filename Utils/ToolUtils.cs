using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using Windows.UI;
using Microsoft.UI.Xaml.Media.Imaging;
using System.IO;
using WinUIMusicPlayer.Model;
using System;
using System.Threading.Tasks;
using System.Data.Common;
using System.Diagnostics;
using TagLib.Riff;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using TagLib;
using NAudio.Wave;
using SQLite;
using Windows.Foundation.Metadata;

namespace WinUIMusicPlayer.Utils
{
    public class ToolUtils
    {
        public enum PlayMode
        {
            SingleLoop,
            ListLoop,
            RandomLoop
        }      

        public static T FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            DependencyObject parent = VisualTreeHelper.GetParent(child);
            if (parent == null)
                return null;
            T parentAsT = parent as T;
            return parentAsT ?? FindParent<T>(parent);
        }

        private static BitmapImage DefaultAlbumCover()
        {
            var uri = new Uri("ms-appx:///Assets/Album.png");
            var bitmapImage = new BitmapImage(uri);
            bitmapImage.DecodePixelWidth = 125;
            bitmapImage.DecodePixelHeight = 125;
            return bitmapImage;
        }

        public static async Task<BitmapImage> GetAlbumCover(Music album, List<Music> musics)
        {
            BitmapImage newCover = album.Cover;
            if (album.Album != "未知专辑")
            {
                var albumSongs = musics.Where(m => m.Album == album.Album);
                foreach (var song in albumSongs)
                {
                    try
                    {
                        using (var file = TagLib.File.Create(song.Path))
                        {
                            if (file.Tag.Pictures.Length > 0)
                            {
                                var picture = file.Tag.Pictures[0];
                                newCover = await ReadBitmapImageAsync(picture, 125);
                                break;                               
                            }
                            else
                            {
                                newCover = DefaultAlbumCover();
                            }
                        }
                    }
                    catch (Exception ex)
                    {                        
                        newCover = DefaultAlbumCover();
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                    }
                }
            }
            else
            {               
                newCover = DefaultAlbumCover();
            }
            return newCover;
        }

        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture,int maxSize) {
            using (var ms = new MemoryStream(picture.Data.Data))
            {
                var bitmapImage = new BitmapImage();
                bitmapImage.ImageOpened += (sender, args) =>
                {
                    double originalWidth = bitmapImage.PixelWidth;
                    double originalHeight = bitmapImage.PixelHeight;
                    double aspectRatio = originalWidth / originalHeight;
                    int newWidth, newHeight;
                    if (originalWidth > originalHeight)
                    {
                        newWidth = maxSize;
                        newHeight = (int)(maxSize / aspectRatio);
                    }
                    else
                    {
                        newHeight = maxSize;
                        newWidth = (int)(maxSize * aspectRatio);
                    }
                    bitmapImage.DecodePixelWidth = newWidth;
                    bitmapImage.DecodePixelHeight = newHeight;
                };
                await bitmapImage.SetSourceAsync(ms.AsRandomAccessStream());
                return bitmapImage;
            }
        }


        public static async Task<BitmapImage> GetImageFromMusic(Music music)
        {
            try
            {
                using (var file = TagLib.File.Create(music.Path))
                {
                    if (file.Tag.Pictures.Length > 0)
                    {
                        IPicture picture = file.Tag.Pictures[0];
                        return await ReadBitmapImageAsync(picture,80);
                    }
                    else
                    {
                        var uri = new Uri("ms-appx:///Assets/Music.png");
                        var bitmapImage = new BitmapImage(uri);
                        return bitmapImage;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"封面读取失败: {ex.Message}");
                var uri = new Uri("ms-appx:///Assets/Music.png");
                var bitmapImage = new BitmapImage(uri);
                return bitmapImage;
            }
        }

        public static List<Music> UpdateFavouriteMusic(List<Music> musicList, Music music)
        {
            if (musicList != null && musicList.Count > 0)
            {
                var index = musicList.FindIndex(m => m.Id == music.Id);
                if (index != -1)
                {
                    musicList[index].isFavorite = music.isFavorite;                    
                }
            }
            return musicList;
        }

        public static List<Music> SortMusicList(string type, string sortOrder, List<Music> musicList)
        {
            if (sortOrder == "A-Z") {
                return musicList.OrderBy(m => m.Title).ToList();
            }
            if (sortOrder == "Artist")
            {
                return musicList.OrderBy(m => m.Author).ToList();
            }
            if (sortOrder == "Album")
            {
                return musicList.OrderBy(m => m.Album).ToList();
            }
            switch (type)
            {
                case "song":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Title).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "folderCover":
                    switch (sortOrder)
                    {                       
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.LastLevelFolderPath).ToList(); ;
                        default:
                            return musicList.ToList();
                    }
                case "folder":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Album).ToList(); ;
                        default:
                            return musicList.ToList();
                    }
                case "artistCover":
                    switch (sortOrder)
                    {                       
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Author).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "artist":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Album).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "albumCover":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.Album).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "album":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderBy(m => m.TrackNumber).ToList();
                        default:
                            return musicList.ToList();
                    }
                case "favour":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderByDescending(m => m.Order).ToList();
                        default:
                            return musicList.ToList();
                    }
                default:
                    return musicList.ToList();
            }
        }

        public static AudioFileInfo GetAudioFileInfo(string filePath)
        {
            try
            {
                using (var reader = new MediaFoundationReader(filePath))
                {
                    int sampleRate = reader.WaveFormat.SampleRate;
                    int channelCount = reader.WaveFormat.Channels;
                    int bitDepth = reader.WaveFormat.BitsPerSample;
                    TimeSpan duration = reader.TotalTime;
                    int bitRate = (int)(reader.Length * 8 / duration.TotalSeconds / 1000);
                    return new AudioFileInfo
                    {
                        SampleRate = sampleRate,
                        ChannelCount = channelCount,
                        BitRate = bitRate,
                        BitDepth = bitDepth,
                        Duration = duration
                    };
                }               
            }
            catch (Exception ex)
            {
                Console.WriteLine($"读取 WAV 文件时出错: {ex.Message}");
                return new AudioFileInfo();
            }
        }
    }
}
