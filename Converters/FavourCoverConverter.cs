using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class FavourCoverConverter:IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (AppSettings.IsPlayListCoverEnabled)
            {
                if (value is Music music && music != null)
                {

                    if (AppData.albumCoverCache.TryGetValue(music.Album, out var cached))
                    {
                        return cached;
                    }
                    // 缓存未命中，开始加载
                    var placeholderBitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
                    _ = LoadImageAsync(music.Path, music.Album, placeholderBitmap);
                    return placeholderBitmap;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
        private async Task LoadImageAsync(string filePath, string album, BitmapImage bitmap)
        {
            await AppData.Semaphore.WaitAsync();
            try
            {
                await ToolUtils.LoadImageAsync(filePath, album, bitmap);
            }
            finally
            {
                AppData.Semaphore.Release();
            }
        }
    }
}
