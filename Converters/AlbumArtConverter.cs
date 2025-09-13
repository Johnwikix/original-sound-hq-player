using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Music music && music != null)
            {
                var placeholderBitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
                if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedCover))
                {
                    placeholderBitmap = cachedCover;
                }
                else
                {
                    _ = LoadImageAsync(music.Path, music.Album, placeholderBitmap);
                }
                return placeholderBitmap;
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            // 通常不需要实现反向转换
            throw new NotImplementedException();
        }

        private async Task LoadImageAsync(string filePath,string album, BitmapImage bitmap)
        {
            try
            {
                // 在后台线程执行耗时的文件I/O
                byte[] albumArtData = await Task.Run(() =>
                {
                    using (var file = TagLib.File.Create(filePath))
                    {
                        var picture = file.Tag.Pictures.FirstOrDefault();
                        return picture?.Data.Data;
                    }
                });

                if (albumArtData != null)
                {
                    // 回到 UI 线程来更新 BitmapImage
                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        using (var stream = new MemoryStream(albumArtData))
                        {
                            try
                            {
                                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
                                if (AppSettings.isCoverCacheEnabled && bitmap != null)
                                {
                                    AppData.albumCoverCache.SetValue(album, bitmap);
                                }
                            }
                            catch (Exception) {
                            }                            
                        }
                    });
                }
            }
            catch (Exception)
            {
                // 错误处理，可忽略或记录
            }
        }
    }
}
