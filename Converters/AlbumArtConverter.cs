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
        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);

        public object Convert(object value, Type targetType, object parameter, string language)
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
                                // 直接使用图片的原始数据，避免额外的内存复制
                                using (var originalStream = new MemoryStream(picture.Data.Data))
                                {
                                    // 解码原始图像
                                    var decoder = await BitmapDecoder.CreateAsync(originalStream.AsRandomAccessStream());
                                    // 计算保持宽高比的缩放尺寸
                                    double aspectRatio = (double)decoder.PixelWidth / decoder.PixelHeight;
                                    uint newWidth, newHeight;
                                    if (aspectRatio > 1) // 宽度大于高度
                                    {
                                        newWidth = (uint)AppSettings.CoverSize;
                                        newHeight = (uint)(AppSettings.CoverSize / aspectRatio);
                                    }
                                    else // 高度大于或等于宽度
                                    {
                                        newHeight = (uint)AppSettings.CoverSize;
                                        newWidth = (uint)(AppSettings.CoverSize * aspectRatio);
                                    }

                                    // 创建缩放后的编码器
                                    using (var inMemoryStream = new InMemoryRandomAccessStream())
                                    {
                                        var encoder = await BitmapEncoder.CreateForTranscodingAsync(inMemoryStream, decoder);
                                        encoder.BitmapTransform.ScaledWidth = newWidth;
                                        encoder.BitmapTransform.ScaledHeight = newHeight;
                                        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                                        await encoder.FlushAsync();

                                        // 优化：直接从流中获取数据，而不是创建新的缓冲区
                                        inMemoryStream.Seek(0);
                                        // 使用 ReadAsync 到一个预先分配的缓冲区，避免新的 byte[] 分配
                                        imageData = new byte[inMemoryStream.Size];
                                        await inMemoryStream.ReadAsync(imageData.AsBuffer(), (uint)imageData.Length, InputStreamOptions.None);
                                    }
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
                    App.MainWindow.DispatcherQueue.TryEnqueue(
                        async () =>
                        {
                            try
                            {
                                await SetSourceAsync(bitmap, imageData);
                                AppData.albumCoverCache.TryAdd(album, bitmap);
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
