using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.Model;
using WinUIMusicPlayer.Utils;

namespace WinUIMusicPlayer.Converters
{
    public class AlbumArtConverter : IValueConverter
    {
        // 使用结构体减少堆分配
        private struct LoadRequest
        {
            public string Key;
            public long Priority;
            public BitmapImage Bitmap;
            public Music Music;
        }

        private struct TaskInfo
        {
            public BitmapImage Bitmap;
            public CancellationTokenSource Cts;
            public long Priority;
            public bool IsProcessing;
        }

        private static readonly ConcurrentDictionary<string, BitmapImage> _pendingImages = new(StringComparer.Ordinal);
        private static readonly ConcurrentDictionary<string, TaskInfo> _loadingTasks = new(StringComparer.Ordinal);

        // 使用 Channel 替代自定义队列，性能更好
        private static readonly System.Threading.Channels.Channel<LoadRequest> _requestChannel =
            System.Threading.Channels.Channel.CreateUnbounded<LoadRequest>(
                new System.Threading.Channels.UnboundedChannelOptions
                {
                    SingleReader = false,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });

        private static readonly SemaphoreSlim _semaphore = new(AppSettings.CoverLoadThreadCount);
        private static long _requestCounter;
        private static readonly CancellationTokenSource _globalCts = new();

        // 对象池，减少 TaskInfo 的堆分配
        private static readonly ConcurrentBag<CancellationTokenSource> _ctsPool = new();

        static AlbumArtConverter()
        {
            // 启动多个处理任务以充分利用线程池
            int workerCount = Math.Max(2, AppSettings.CoverLoadThreadCount / 2);
            for (int i = 0; i < workerCount; i++)
            {
                _ = ProcessQueueAsync();
            }

            // 定期清理过期任务（减少内存占用）
            _ = PeriodicCleanupAsync();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static CancellationTokenSource RentCts()
        {
            if (_ctsPool.TryTake(out var cts))
            {
                try
                {
                    // 重置取消状态（如果可能）
                    return cts;
                }
                catch
                {
                    cts?.Dispose();
                }
            }
            return new CancellationTokenSource();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void ReturnCts(CancellationTokenSource cts)
        {
            if (cts != null && !cts.IsCancellationRequested && _ctsPool.Count < 50)
            {
                _ctsPool.Add(cts);
            }
            else
            {
                cts?.Dispose();
            }
        }

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not Music music)
                return null;

            var album = music.Album;

            // 快速路径：检查缓存
            if (AppData.albumCoverCache.TryGetValue(album, out var cached))
                return cached;

            // 检查是否已有待处理的图片
            if (_pendingImages.TryGetValue(album, out var existingBitmap))
            {
                // 提升优先级
                if (_loadingTasks.TryGetValue(album, out var existing))
                {
                    var newPriority = Interlocked.Increment(ref _requestCounter);

                    // 使用结构体更新避免额外分配
                    var updated = existing;
                    updated.Priority = newPriority;
                    _loadingTasks[album] = updated;

                    // 如果还未开始处理，重新入队
                    if (!existing.IsProcessing)
                    {
                        _requestChannel.Writer.TryWrite(new LoadRequest
                        {
                            Key = album,
                            Priority = newPriority,
                            Bitmap = existingBitmap,
                            Music = music
                        });
                    }
                }
                return existingBitmap;
            }

            // 创建新的加载请求
            var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
            if (_pendingImages.TryAdd(album, bitmap))
            {
                var priority = Interlocked.Increment(ref _requestCounter);
                var cts = RentCts();

                var taskInfo = new TaskInfo
                {
                    Bitmap = bitmap,
                    Cts = cts,
                    Priority = priority,
                    IsProcessing = false
                };

                if (_loadingTasks.TryAdd(album, taskInfo))
                {
                    _requestChannel.Writer.TryWrite(new LoadRequest
                    {
                        Key = album,
                        Priority = priority,
                        Bitmap = bitmap,
                        Music = music
                    });
                }
                else
                {
                    ReturnCts(cts);
                }

                return bitmap;
            }

            // 如果添加失败，返回已存在的
            return _pendingImages.TryGetValue(album, out var fallback) ? fallback : null;
        }

        private static async Task ProcessQueueAsync()
        {
            var reader = _requestChannel.Reader;

            // 本地缓存，减少字典访问
            LoadRequest[] batchBuffer = new LoadRequest[10];
            int batchCount = 0;

            while (!_globalCts.Token.IsCancellationRequested)
            {
                try
                {
                    // 批量读取请求
                    while (batchCount < batchBuffer.Length && reader.TryRead(out var request))
                    {
                        batchBuffer[batchCount++] = request;
                    }

                    if (batchCount == 0)
                    {
                        await reader.WaitToReadAsync(_globalCts.Token);
                        continue;
                    }

                    // 按优先级排序（就地排序，无额外分配）
                    Array.Sort(batchBuffer, 0, batchCount,
                        Comparer<LoadRequest>.Create((a, b) => b.Priority.CompareTo(a.Priority)));

                    // 处理批次中的请求
                    for (int i = 0; i < batchCount; i++)
                    {
                        var request = batchBuffer[i];

                        // 快速检查是否已缓存
                        if (AppData.albumCoverCache.ContainsKey(request.Key))
                        {
                            CleanupTask(request.Key);
                            continue;
                        }

                        if (!_loadingTasks.TryGetValue(request.Key, out var taskInfo))
                            continue;

                        // 标记为正在处理
                        var updated = taskInfo;
                        updated.IsProcessing = true;
                        _loadingTasks[request.Key] = updated;

                        // 异步加载（不等待，避免阻塞队列处理）
                        _ = LoadImageCoreAsync(request.Key, request.Music, taskInfo.Bitmap, taskInfo.Cts.Token);
                    }

                    batchCount = 0;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Queue processing error: {ex.Message}");
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static async Task LoadImageCoreAsync(string key, Music music, BitmapImage bitmap, CancellationToken ct)
        {
            try
            {
                await _semaphore.WaitAsync(ct);

                try
                {
                    // 最后一次缓存检查
                    if (AppData.albumCoverCache.ContainsKey(key) || ct.IsCancellationRequested)
                        return;

                    await ToolUtils.LoadImageAsync(music, bitmap, ct);
                }
                finally
                {
                    _semaphore.Release();
                }
            }
            catch (OperationCanceledException)
            {
                // 正常取消，不记录
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Load error for {key}: {ex.Message}");
            }
            finally
            {
                CleanupTask(key);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void CleanupTask(string key)
        {
            if (_loadingTasks.TryRemove(key, out var taskInfo))
            {
                ReturnCts(taskInfo.Cts);
            }
            _pendingImages.TryRemove(key, out _);
        }

        // 定期清理长时间未完成的任务（防止内存泄漏）
        private static async Task PeriodicCleanupAsync()
        {
            var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));

            while (!_globalCts.Token.IsCancellationRequested)
            {
                try
                {
                    await timer.WaitForNextTickAsync(_globalCts.Token);

                    // 取消低优先级任务（保留最近的50个）
                    if (_loadingTasks.Count > 50)
                    {
                        var threshold = Interlocked.Read(ref _requestCounter) - 50;

                        foreach (var kvp in _loadingTasks)
                        {
                            if (kvp.Value.Priority < threshold && !kvp.Value.IsProcessing)
                            {
                                if (_loadingTasks.TryRemove(kvp.Key, out var removed))
                                {
                                    removed.Cts?.Cancel();
                                    ReturnCts(removed.Cts);
                                    _pendingImages.TryRemove(kvp.Key, out _);
                                }
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        // 供外部调用：立即取消所有待处理任务
        public static void CancelAllPending()
        {
            foreach (var kvp in _loadingTasks)
            {
                if (!kvp.Value.IsProcessing)
                {
                    kvp.Value.Cts?.Cancel();
                }
            }
        }
    }
}