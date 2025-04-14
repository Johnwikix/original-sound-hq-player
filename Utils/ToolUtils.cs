using ATL;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using TagLib;
using WinUIMusicPlayer.Model;

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
                            }
                            else
                            {
                                newCover = DefaultAlbumCover();
                            }
                            return newCover;
                        }
                    }
                    catch (Exception ex)
                    {
                        newCover = DefaultAlbumCover();
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                        return newCover;
                    }
                }
            }
            else
            {
                newCover = DefaultAlbumCover();
                return newCover;
            }
            return newCover;
        }

        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture, int maxSize)
        {
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

        public static T FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                {
                    return typedChild;
                }
                else
                {
                    var foundChild = FindVisualChild<T>(child);
                    if (foundChild != null)
                    {
                        return foundChild;
                    }
                }
            }
            return null;
        }


        public static async Task<BitmapImage> GetImageFromMusic(Music music,int size = 100)
        {
            try
            {
                Track theTrack = new Track(music.Path);
                PictureInfo cover = theTrack.EmbeddedPictures.FirstOrDefault();
                if (cover != null)
                {
                    Image image = Image.FromStream(new MemoryStream(cover.PictureData));
                    using (MemoryStream memoryStream = new MemoryStream())
                    {
                        // 将 System.Drawing.Image 保存到内存流
                        image.Save(memoryStream, System.Drawing.Imaging.ImageFormat.Png);
                        memoryStream.Position = 0;
                        // 创建一个 BitmapImage 对象
                        BitmapImage bitmapImage = new BitmapImage();
                        // 将内存流转换为 IRandomAccessStream
                        var randomAccessStream = memoryStream.AsRandomAccessStream();
                        // 从流中加载图像
                        await bitmapImage.SetSourceAsync(randomAccessStream);
                        return bitmapImage;
                    }
                }
                else {
                    var uri = new Uri("ms-appx:///Assets/Music.png");
                    var bitmapImage = new BitmapImage(uri);
                    return bitmapImage;
                }                
                //using (var file = TagLib.File.Create(music.Path))
                //{
                //    if (file.Tag.Pictures.Length > 0)
                //    {
                //        IPicture picture = file.Tag.Pictures[0];
                //        return await ReadBitmapImageAsync(picture, size);
                //    }
                //    else
                //    {
                //        var uri = new Uri("ms-appx:///Assets/Music.png");
                //        var bitmapImage = new BitmapImage(uri);
                //        return bitmapImage;
                //    }
                //}
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
            if (sortOrder == "A-Z")
            {
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
                case "playList":
                    switch (sortOrder)
                    {
                        case "DefaultOrder":
                            return musicList.OrderByDescending(m => m.PlayListOrder).ToList();
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

        public static bool IsMusicFile(string fileType)
        {
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg", ".m4a" };
            return musicExtensions.Contains(fileType.ToLower());
        }

        public static List<Music> UpdateMusicInList(List<Music> musicList, Music newMusic)
        {
            for (int i = 0; i < musicList.Count; i++)
            {
                if (musicList[i].Id == newMusic.Id)
                {
                    musicList[i] = newMusic;                    
                }
            }
            return musicList;
        }

        public static async Task<BitmapImage> LoadAlbumCover(Music music)
        {
            if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
            {
                return cachedCover;
            }
            else
            {
                return await GetImageFromMusic(music);
            }
        }
    }
}
