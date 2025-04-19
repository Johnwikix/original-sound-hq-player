using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using TagLib;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Services;

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

        public static async Task<BitmapImage> GetAlbumCover(Music album)
        {
            BitmapImage newCover = album.Cover;
            List<Music> musics =(await MusicDatabaseService.GetAlbumMusicAsync(album.Album)).Where(m => m.Extension.ToLower() != "wav").ToList();
            if (album.Album != "未知专辑")
            {
                if (musics == null || musics.Count() == 0)
                {
                    return DefaultAlbumCover();
                }
                foreach (var song in musics)
                {
                    try
                    {
                        using (var file = TagLib.File.Create(song.Path))
                        {
                            if (file.Tag.Pictures.Length > 0)
                            {
                                var picture = file.Tag.Pictures[0];
                                newCover = await ReadBitmapImageAsync(picture, 150);
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

        public static async Task<BitmapImage> ReadBitmapImageAsync(IPicture picture, int maxSize = 0)
        {
            using (var ms = new MemoryStream(picture.Data.Data))
            {
                var bitmapImage = new BitmapImage();
                if (maxSize != 0)
                {
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
                }

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


        public static async Task<BitmapImage> ConvertByteArrayToBitmapImage(byte[] imageData)
        {
            try
            {
                if (imageData == null || imageData.Length == 0)
                    return DefaultAlbumCover();
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using (var dataWriter = new DataWriter(stream.GetOutputStreamAt(0)))
                    {
                        dataWriter.WriteBytes(imageData);
                        await dataWriter.StoreAsync();
                    }
                    var bitmapImage = new BitmapImage();
                    await bitmapImage.SetSourceAsync(stream);
                    return bitmapImage;
                }
            }
            catch(Exception ex) {
                return DefaultAlbumCover();
            }            
        }

        public static byte[] GetRawImage(Music music)
        {
            try
            {
                using (TagLib.File audioFile = TagLib.File.Create(music.Path))
                {
                    // 获取音频文件中的图片数组
                    IPicture[] pictures = audioFile.Tag.Pictures;

                    if (pictures.Length > 0)
                    {
                        // 取第一张图片作为封面
                        IPicture coverPicture = pictures[0];

                        // 获取封面图片的字节数组
                        byte[] coverBytes = coverPicture.Data.Data;
                        return coverBytes;
                    }
                    else
                    {
                        Console.WriteLine("未找到封面图片信息。");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"发生错误: {ex.Message}");
            }
            return null;
        }

        public static async Task<BitmapImage> GetImageFromMusic(Music music, int size = 100)
        {
            try
            {
                using (var file = TagLib.File.Create(music.Path))
                {
                    if (file.Tag.Pictures.Length > 0)
                    {
                        IPicture picture = file.Tag.Pictures[0];
                        return await ReadBitmapImageAsync(picture, size);
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
            var musicExtensions = new[] { ".mp3", ".wav", ".flac", ".wma", ".aac", ".ogg",".aiff", ".m4a",".dsf",".dff" };
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

        public static async Task<byte[]> ImageToByteArray(Microsoft.UI.Xaml.Controls.Image imageControl, double scaleFactor = 1)
        {
            byte[] buffer = null;
            if (imageControl.Source is BitmapImage bitmapImage)
            {
                // 使用 RenderTargetBitmap 捕获图像
                var renderTargetBitmap = new RenderTargetBitmap();
                await renderTargetBitmap.RenderAsync(imageControl, (int)(bitmapImage.PixelWidth / scaleFactor), (int)(bitmapImage.PixelHeight / scaleFactor));
                Debug.WriteLine($"bitmapImage长宽:{bitmapImage.PixelHeight} {bitmapImage.PixelWidth}");
                // 获取像素
                var pixelBuffer = await renderTargetBitmap.GetPixelsAsync();
                var pixels = pixelBuffer.ToArray();

                // 创建编码器并写入流
                var stream = new InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                Debug.WriteLine($"renderTargetBitmap长宽:{(uint)renderTargetBitmap.PixelHeight} {(uint)renderTargetBitmap.PixelWidth}");
                // 设置像素数据
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Premultiplied,
                    (uint)renderTargetBitmap.PixelWidth,
                    (uint)renderTargetBitmap.PixelHeight,
                    96.0, // DPI X
                    96.0, // DPI Y
                    pixels);

                // 刷新编码器
                await encoder.FlushAsync();

                // 读取流到字节数组
                stream.Seek(0);
                buffer = new byte[stream.Size];
                await stream.AsStream().ReadAsync(buffer, 0, buffer.Length);

            }
            return buffer;
        }

    }
}
