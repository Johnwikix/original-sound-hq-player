using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _pendingImages = new();
        private static readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is Music music && music is not null)
            {
                // 检查缓存
                if (AppData.albumCoverCache.TryGetValue(music.Album, out var cached))
                {
                    return cached;
                }

                string key = music.Album;

                // 如果已经有正在加载的任务，返回共享的 BitmapImage
                if (_pendingImages.TryGetValue(key, out var existingBitmap))
                {
                    return existingBitmap;
                }

                // 创建新的 BitmapImage 并开始加载
                var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };

                // 尝试添加到待加载字典，如果已存在则使用已有的
                var sharedBitmap = _pendingImages.GetOrAdd(key, bitmap);

                // 只有当前线程成功添加时才启动加载任务
                if (sharedBitmap == bitmap)
                {
                    // 确保只有一个加载任务
                    _loadingTasks.GetOrAdd(key, _ => LoadImageAsync(sharedBitmap, music, key));
                }

                return sharedBitmap;
            }
            return null;
        }

        private static async Task LoadImageAsync(BitmapImage bitmap, Music music, string key)
        {
            try
            {
                await _semaphore.WaitAsync();

                try
                {
                    // 再次检查缓存（可能在等待期间已被其他任务加载）
                    if (AppData.albumCoverCache.ContainsKey(key))
                    {
                        return;
                    }

                    await ToolUtils.LoadImageAsync(music, bitmap, CancellationToken.None);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (Exception ex)
            {
                // 记录错误日志
                System.Diagnostics.Debug.WriteLine($"Failed to load album art for {key}: {ex.Message}");
            }
            finally
            {
                // 清理
                _pendingImages.TryRemove(key, out _);
                _loadingTasks.TryRemove(key, out _);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}