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
                                var uri = new Uri("ms-appx:///Assets/Album.png");
                                var bitmapImage = new BitmapImage(uri);
                                bitmapImage.DecodePixelWidth = 125;
                                bitmapImage.DecodePixelHeight = 125;
                                newCover = bitmapImage;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"读取专辑 {album.Album} 封面失败: {ex.Message}");
                    }
                }
            }
            else
            {
                var uri = new Uri("ms-appx:///Assets/Album.png");
                var bitmapImage = new BitmapImage(uri);
                bitmapImage.DecodePixelWidth = 125;
                bitmapImage.DecodePixelHeight = 125;
                newCover = bitmapImage;
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
    }
}
