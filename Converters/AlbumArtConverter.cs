using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.UI.Core;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        private static readonly SemaphoreSlim _semaphore = new SemaphoreSlim(AppSettings.CoverLoadThreadCount);
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
            throw new NotImplementedException();
        }

        private async Task LoadImageAsync(string filePath,string album, BitmapImage bitmap)
        {
            await _semaphore.WaitAsync();
            try
            {
                byte[] imageData = null;
                await Task.Run(() =>
                {
                    try
                    {
                        using (var file = TagLib.File.Create(filePath))
                        {
                            var picture = file.Tag.Pictures.FirstOrDefault();
                            if (picture?.Data.Data != null)
                            {
                                imageData = picture.Data.Data;
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                });
                if (imageData != null)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            using (var stream = new MemoryStream(imageData))
                            {
                                bitmap.SetSource(stream.AsRandomAccessStream());
                                if (AppSettings.isCoverCacheEnabled && bitmap != null)
                                {
                                    AppData.albumCoverCache.SetValue(album, bitmap);
                                }
                            }
                        }
                        catch (Exception)
                        {
                        }
                    });
                }
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
