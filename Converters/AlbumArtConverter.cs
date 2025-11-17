using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        private static readonly ConcurrentDictionary<string, BitmapImage> _pendingImages = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
        private static readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
        // 跟踪哪些 Music.Id 正在使用哪个 Album key
        private static readonly ConcurrentDictionary<string, HashSet<int>> _albumToMusicIds = new();
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

                // 记录这个 Music.Id 使用了这个 Album
                RegisterMusicForAlbum(music.Id, key);

                // 如果已经有正在加载的任务,返回共享的 BitmapImage
                if (_pendingImages.TryGetValue(key, out var existingBitmap))
                {
                    return existingBitmap;
                }

                // 创建新的 BitmapImage 并开始加载
                var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };

                // 尝试添加到待加载字典,如果已存在则使用已有的
                var sharedBitmap = _pendingImages.GetOrAdd(key, bitmap);

                // 只有当前线程成功添加时才启动加载任务
                if (sharedBitmap == bitmap)
                {
                    var cts = new CancellationTokenSource();
                    _cancellationTokens.TryAdd(key, cts);

                    // 确保只有一个加载任务
                    _loadingTasks.GetOrAdd(key, _ => LoadImageAsync(sharedBitmap, music, key, cts.Token));
                }

                return sharedBitmap;
            }
            return null;
        }

        /// <summary>
        /// 当 Music 离开可视区域时调用
        /// </summary>
        public static void OnMusicUnloaded(int musicId)
        {
            // 查找这个 musicId 对应的 album key
            string albumKey = null;
            foreach (var kvp in _albumToMusicIds)
            {
                lock (kvp.Value)
                {
                    if (kvp.Value.Contains(musicId))
                    {
                        albumKey = kvp.Key;
                        // 移除这个 musicId
                        kvp.Value.Remove(musicId);
                        break;
                    }
                }
            }

            if (albumKey == null) return;

            // 如果这个 album 没有其他 Music 在使用,取消正在执行的加载任务
            if (_albumToMusicIds.TryGetValue(albumKey, out var musicIds))
            {
                bool shouldCancel = false;
                lock (musicIds)
                {
                    shouldCancel = musicIds.Count == 0;
                }

                if (shouldCancel)
                {
                    // 取消正在执行的任务
                    if (_loadingTasks.ContainsKey(albumKey) &&
                        _cancellationTokens.TryGetValue(albumKey, out var cts))
                    {
                        cts.Cancel();
                        System.Diagnostics.Debug.WriteLine($"Cancelled loading task for album: {albumKey}");
                    }

                    // 清理空的 HashSet
                    _albumToMusicIds.TryRemove(albumKey, out _);
                }
            }
        }

        private static void RegisterMusicForAlbum(int musicId, string albumKey)
        {
            _albumToMusicIds.AddOrUpdate(
                albumKey,
                _ => new HashSet<int> { musicId },
                (_, set) =>
                {
                    lock (set)
                    {
                        set.Add(musicId);
                    }
                    return set;
                });
        }

        private static async Task LoadImageAsync(BitmapImage bitmap, Music music, string key, CancellationToken cancellationToken)
        {
            try
            {
                await _semaphore.WaitAsync(cancellationToken);
                try
                {
                    // 检查是否已取消
                    cancellationToken.ThrowIfCancellationRequested();

                    // 再次检查缓存(可能在等待期间已被其他任务加载)
                    if (AppData.albumCoverCache.ContainsKey(key))
                    {
                        return;
                    }

                    await ToolUtils.LoadImageAsync(music, bitmap, cancellationToken);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // 任务被取消,正常情况
                System.Diagnostics.Debug.WriteLine($"Album art loading cancelled for {key}");
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
                _cancellationTokens.TryRemove(key, out _);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}