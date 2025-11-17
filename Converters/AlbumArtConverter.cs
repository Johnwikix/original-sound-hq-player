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

        // album -> set of musicIds (使用 ConcurrentDictionary<int,byte> 作为线程安全的 set)
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _albumToMusicIds = new();

        // musicId -> albumKey 的反向索引，避免在卸载时遍历所有 album
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
            // 直接通过反向索引查找关联的 albumKey
            if (!_musicIdToAlbumKey.TryRemove(musicId, out var albumKey) || albumKey is null)
            {
                return;
            }

            // 从 album -> musicId 集合中移除该 id
            if (_albumToMusicIds.TryGetValue(albumKey, out var set))
            {
                set.TryRemove(musicId, out _);

                // 如果集合为空，说明没有可视项再需要该 album，尝试取消加载
                if (set.IsEmpty)
                {
                    // 取消 CTS（如果存在）
                    if (_cancellationTokens.TryRemove(albumKey, out var cts))
                    {
                        try { cts.Cancel(); }
                        catch { }
                        cts.Dispose();
                    }

                    // 清理 pending 与 loadingTask（LoadImageAsync 的 finally 也会尝试清理，但这里做一次快速清理以释放引用）
                    _pendingImages.TryRemove(albumKey, out _);
                    _loadingTasks.TryRemove(albumKey, out _);
                    _albumToMusicIds.TryRemove(albumKey, out _);
                }
            }
        }

        private static void RegisterMusicForAlbum(int musicId, string albumKey)
        {
            // 如果此 musicId 之前已关联其他 album，先从旧集合中移除
            if (_musicIdToAlbumKey.TryGetValue(musicId, out var existingKey) && existingKey != albumKey)
            {
                if (_albumToMusicIds.TryGetValue(existingKey, out var oldSet))
                {
                    oldSet.TryRemove(musicId, out _);
                    if (oldSet.IsEmpty)
                    {
                        // 清理旧集合的残留（非必须，但能尽早释放资源）
                        _albumToMusicIds.TryRemove(existingKey, out _);
                        if (_cancellationTokens.TryRemove(existingKey, out var oldCts))
                        {
                            try { oldCts.Cancel(); } catch { }
                            oldCts.Dispose();
                        }
                    }
                }
            }

            // 记录反向索引
            _musicIdToAlbumKey[musicId] = albumKey;

            // 将 musicId 添加到 album -> set 中（线程安全）
            _albumToMusicIds.AddOrUpdate(
                albumKey,
                key =>
                {
                    var cd = new ConcurrentDictionary<int, byte>();
                    cd.TryAdd(musicId, 0);
                    return cd;
                },
                (_, existing) =>
                {
                    existing.TryAdd(musicId, 0);
                    return existing;
                });
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