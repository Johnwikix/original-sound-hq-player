using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Core;
using WinUIMusicPlayer.Model;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        private static readonly SemaphoreSlim _semaphore = new(4);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Music music && music != null)
            {
                // 尝试从缓存中获取 byte[] 数组
                if (AppData.albumCoverCache.TryGetValue(music.Album, out var cachedData))
                {
                    // 如果命中，直接从 byte[] 数组创建 BitmapImage
                    var cachedBitmap = new BitmapImage();
                    _ = SetSourceAsync(cachedBitmap, cachedData);
                    return cachedBitmap;
                }
                // 缓存未命中，开始加载
                var placeholderBitmap = new BitmapImage();
                _ = LoadImageAsync(music.Path, music.Album, placeholderBitmap);
                return placeholderBitmap;
            }
            return null;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private async Task LoadImageAsync(string filePath, string album, BitmapImage bitmap)
        {
            await _semaphore.WaitAsync();
            try
            {
                byte[] imageData = null;
                await Task.Run(async () =>
                {
                    try
                    {
                        using (var file = TagLib.File.Create(filePath))
                        {
                            var picture = file.Tag.Pictures.FirstOrDefault();
                            if (picture?.Data.Data != null)
                            {
                                using (var originalStream = new InMemoryRandomAccessStream())
                                {
                                    await originalStream.WriteAsync(picture.Data.Data.AsBuffer());
                                    originalStream.Seek(0);
                                    // 解码原始图像
                                    var decoder = await BitmapDecoder.CreateAsync(originalStream);
                                    // 创建缩放后的编码器
                                    var inMemoryStream = new InMemoryRandomAccessStream();
                                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(inMemoryStream, decoder);
                                    encoder.BitmapTransform.ScaledWidth = (uint)AppSettings.CoverSize;
                                    encoder.BitmapTransform.ScaledHeight = (uint)AppSettings.CoverSize;
                                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                                    await encoder.FlushAsync();
                                    // 将缩放后的图像数据转换为 byte[] 数组
                                    var outputData = new byte[inMemoryStream.Size];
                                    await inMemoryStream.ReadAsync(outputData.AsBuffer(), (uint)inMemoryStream.Size, InputStreamOptions.None);
                                    imageData = outputData;
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                });

                if (imageData != null)
                {
                    AppData.albumCoverCache.TryAdd(album, imageData);
                    App.MainWindow.DispatcherQueue.TryEnqueue(
                        async () =>
                        {
                            try
                            {
                                await SetSourceAsync(bitmap, imageData);
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
        private async Task SetSourceAsync(BitmapImage bitmap, byte[] data)
        {
            using (var stream = new MemoryStream(data))
            {
                await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
            }
        }        
    }
}
