//using Microsoft.UI.Xaml.Data;
//using Microsoft.UI.Xaml.Media.Imaging;
//using System;
//using System.Threading;
//using System.Threading.Tasks;
//using WinUIMusicPlayer.Model;
//using WinUIMusicPlayer.Utils;

//namespace WinUIMusicPlayer.Converters
//{
//    public class FavourCoverConverter : IValueConverter
//    {
//        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);
//        public object Convert(object value, Type targetType, object parameter, string language)
//        {
//            if (AppSettings.IsPlayListCoverEnabled)
//            {
//                if (value is Music music && music is not null)
//                {

//                    if (AppData.albumCoverCache.TryGetValue(music.Album, out var cached))
//                    {
//                        return cached;
//                    }
//                    // 缓存未命中，开始加载
//                    var placeholderBitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
//                    _ = LoadImageAsync(placeholderBitmap, music);
//                    return placeholderBitmap;
//                }
//            }
//            return null;
//        }

//        public object ConvertBack(object value, Type targetType, object parameter, string language)
//        {
//            throw new NotImplementedException();
//        }
//        private static async Task LoadImageAsync(BitmapImage bitmap, Music music)
//        {
//            await _semaphore.WaitAsync();
//            try
//            {
//                await ToolUtils.LoadImageAsync(music, bitmap);
//            }
//            finally
//            {
//                _semaphore.Release();
//            }
//        }
//    }
//}
