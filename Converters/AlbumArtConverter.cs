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

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is not Music music) return null;

            // ImageHash 已知时才能走字典缓存
            if (!string.IsNullOrEmpty(music.ImageHash))
            {
                if (AppData.albumCoverCache.TryGetValue(music.ImageHash, out var cached))
                    return cached;
            }

            string pendingKey = music.Id.ToString();
            if (_pendingImages.TryGetValue(pendingKey, out var existingBitmap))
                return existingBitmap;

            var bitmap = new BitmapImage { DecodePixelWidth = AppSettings.CoverSize };
            var sharedBitmap = _pendingImages.GetOrAdd(pendingKey, bitmap);

            if (sharedBitmap == bitmap)
            {
                var cts = new CancellationTokenSource();
                _cancellationTokens[pendingKey] = cts;
                _loadingTasks.GetOrAdd(pendingKey, _ =>
                    LoadImageAsync(sharedBitmap, music, pendingKey, cts.Token));
            }

            return sharedBitmap;
        }

        public static void OnMusicUnloaded(int musicId)
        {
            string key = musicId.ToString();
            if (_cancellationTokens.TryRemove(key, out var cts))
            {
                try { cts.Cancel(); } catch { }
                cts.Dispose();
            }
            _pendingImages.TryRemove(key, out _);
            _loadingTasks.TryRemove(key, out _);
        }

        private static async Task LoadImageAsync(
            BitmapImage bitmap, Music music, string pendingKey, CancellationToken ct)
        {
            try
            {
                await ToolUtils.LoadImageAsync(music, bitmap, ct).ConfigureAwait(true);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { }
            finally
            {
                _pendingImages.TryRemove(pendingKey, out _);
                _loadingTasks.TryRemove(pendingKey, out _);
                _cancellationTokens.TryRemove(pendingKey, out _);
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}