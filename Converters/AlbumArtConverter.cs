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
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellationTokens = new();
        private static readonly ConcurrentDictionary<string, Task> _loadingTasks = new();
        private static readonly ConcurrentDictionary<string, int> _albumRefCounts = new();
        private static readonly ConcurrentDictionary<int, string> _musicIdToAlbumKey = new();
        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not Music music)
            {
                return null;
            }

            string key = music.Album ?? string.Empty;

            // 缓存命中快速返回
            if (AppData.albumCoverCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // 注册 musicId -> album 的映射（线程安全）
            RegisterMusicForAlbum(music.Id, key);

            // 如果已有正在加载或已创建的 BitmapImage，重用它
            if (_pendingImages.TryGetValue(key, out var existingBitmap))
            {
                return existingBitmap;
            }

            // 创建 BitmapImage（仅在确实需要时）
            var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };

            // 尝试把 bitmap 放入 pending 集合，若已有则使用已有的
            var sharedBitmap = _pendingImages.GetOrAdd(key, bitmap);

            // 只有当前线程成功添加才启动加载任务
            if (sharedBitmap == bitmap)
            {
                var cts = new CancellationTokenSource();
                _cancellationTokens[key] = cts;

                // 启动或复用加载任务
                _loadingTasks.GetOrAdd(key, _ => LoadImageAsync(sharedBitmap, music, key, cts.Token));
            }

            return sharedBitmap;
        }

        /// <summary>
        /// 当 Music 离开可视区域时调用，尝试取消未必仍需继续的加载任务
        /// </summary>
        public static void OnMusicUnloaded(int musicId)
        {
            // 通过反向映射快速定位 albumKey
            if (!_musicIdToAlbumKey.TryRemove(musicId, out var albumKey) || string.IsNullOrEmpty(albumKey))
            {
                return;
            }

            // 原子减少引用计数
            int newCount = _albumRefCounts.AddOrUpdate(albumKey, 0, (_, v) => Math.Max(0, v - 1));
            if (newCount == 0)
            {
                // 取消并释放 CTS（如果存在）
                if (_cancellationTokens.TryRemove(albumKey, out var cts))
                {
                    try { cts.Cancel(); } catch { }
                    cts.Dispose();
                }

                // 清理其它引用以便 GC 回收
                _pendingImages.TryRemove(albumKey, out _);
                _loadingTasks.TryRemove(albumKey, out _);
                _albumRefCounts.TryRemove(albumKey, out _);
            }
        }

        private static void RegisterMusicForAlbum(int musicId, string albumKey)
        {
            // 如果此前关联了不同的 album，先减少旧 album 的计数
            if (_musicIdToAlbumKey.TryGetValue(musicId, out var existingKey) && existingKey != albumKey)
            {
                int newOldCount = _albumRefCounts.AddOrUpdate(existingKey, 0, (_, v) => Math.Max(0, v - 1));
                if (newOldCount == 0)
                {
                    // 清理旧 album 的资源（快速释放引用）
                    _albumRefCounts.TryRemove(existingKey, out _);
                    if (_cancellationTokens.TryRemove(existingKey, out var oldCts))
                    {
                        try { oldCts.Cancel(); } catch { }
                        oldCts.Dispose();
                    }
                    _pendingImages.TryRemove(existingKey, out _);
                    _loadingTasks.TryRemove(existingKey, out _);
                }
            }

            // 记录反向索引（覆盖以前的映射）
            _musicIdToAlbumKey[musicId] = albumKey;

            // 增加 album 的引用计数
            _albumRefCounts.AddOrUpdate(albumKey, 1, (_, v) => v + 1);
        }

        private static async Task LoadImageAsync(BitmapImage bitmap, Music music, string key, CancellationToken cancellationToken)
        {
            try
            {
                await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // 再次检查缓存（在等待信号量期间可能被其它线程加载）
                    if (AppData.albumCoverCache.ContainsKey(key))
                    {
                        return;
                    }

                    await ToolUtils.LoadImageAsync(music, bitmap, cancellationToken).ConfigureAwait(true);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                System.Diagnostics.Debug.WriteLine($"Album art loading cancelled for {key}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load album art for {key}: {ex.Message}");
            }
            finally
            {
                // 尝试清理引用（允许并发其它路径也做清理，使用 TryRemove）
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