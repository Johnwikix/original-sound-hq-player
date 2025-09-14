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
                await Task.Run(async () =>
                {
                    try
                    {
                        using (var file = TagLib.File.Create(filePath))
                        {
                            var picture = file.Tag.Pictures.FirstOrDefault();
                            if (picture?.Data.Data != null)
                            {
                                // 直接使用图片的原始数据流
                                using (var originalStream = new MemoryStream(picture.Data.Data))
                                {
                                    // 解码原始图像
                                    var decoder = await BitmapDecoder.CreateAsync(originalStream.AsRandomAccessStream());

                                    // 计算保持宽高比的缩放尺寸
                                    double aspectRatio = (double)decoder.PixelWidth / decoder.PixelHeight;
                                    uint newWidth, newHeight;
                                    if (aspectRatio > 1)
                                    {
                                        newWidth = (uint)AppSettings.CoverSize;
                                        newHeight = (uint)(AppSettings.CoverSize / aspectRatio);
                                    }
                                    else
                                    {
                                        newHeight = (uint)AppSettings.CoverSize;
                                        newWidth = (uint)(AppSettings.CoverSize * aspectRatio);
                                    }

                                    // 创建缩放后的流，避免byte[]中间分配
                                    var resizedStream = new InMemoryRandomAccessStream();
                                    var encoder = await BitmapEncoder.CreateForTranscodingAsync(resizedStream, decoder);
                                    encoder.BitmapTransform.ScaledWidth = newWidth;
                                    encoder.BitmapTransform.ScaledHeight = newHeight;
                                    encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                                    await encoder.FlushAsync();

                                    // 在UI线程中设置bitmap源
                                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                                    {
                                        try
                                        {
                                            resizedStream.Seek(0);
                                            await bitmap.SetSourceAsync(resizedStream);
                                            AppData.albumCoverCache.TryAdd(album, bitmap);
                                            resizedStream.Dispose(); // 在使用完成后释放
                                        }
                                        catch (Exception)
                                        {
                                            resizedStream.Dispose(); // 异常时也要释放
                                        }
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception)
                    {
                        // 静默处理异常
                    }
                });
            }
            finally
            {
                _semaphore.Release();
            }
        }            
    }
}
