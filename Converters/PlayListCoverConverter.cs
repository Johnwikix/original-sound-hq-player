using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class PlayListCoverConverter:IValueConverter
    {
        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);
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
                    _ = LoadImageAsync(music.Path, music.Album, placeholderBitmap, music);
                    return placeholderBitmap;
                }
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
        private async Task LoadImageAsync(string filePath, string album, BitmapImage bitmap, Music music)
        {
            await _semaphore.WaitAsync();
            try
            {
                await ToolUtils.LoadImageAsync(filePath, album, bitmap, music);
            }
            finally
            {
                _semaphore.Release();
            }
        }
    }
}
