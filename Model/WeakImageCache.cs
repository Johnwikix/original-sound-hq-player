using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace WinUIMusicPlayer.Model
{
    public class WeakImageCache
    {
        private readonly ConcurrentDictionary<string, WeakReference<BitmapImage>> _cache =
            new ConcurrentDictionary<string, WeakReference<BitmapImage>>();

        private readonly Timer _cleanupTimer;

        public WeakImageCache()
        {
            // 每5分钟清理一次失效的弱引用
            _cleanupTimer = new Timer(CleanupExpiredReferences, null,
                TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(20));
        }

        public bool TryGetValue(string key, out BitmapImage value)
        {
            value = null;

            if (_cache.TryGetValue(key, out var weakRef))
            {
                if (weakRef.TryGetTarget(out value))
                {
                    return true;
                }
                else
                {
                    // 弱引用已失效，移除
                    _cache.TryRemove(key, out _);
                }
            }

            return false;
        }
        // 新增：检查键是否存在（类似ContainsKey）
        public bool ContainsKey(string key)
        {
            if (_cache.TryGetValue(key, out var weakRef))
            {
                if (weakRef.TryGetTarget(out _))
                {
                    return true;
                }
                else
                {
                    // 弱引用已失效，移除
                    _cache.TryRemove(key, out _);
                    return false;
                }
            }

            return false;
        }
        public bool UpdateIfExists(string key, BitmapImage newValue)
        {
            if (ContainsKey(key) && newValue != null)
            {
                SetValue(key, newValue);
                return true;
            }
            return false;
        }
        public void SetValue(string key, BitmapImage value)
        {
            if (value != null)
            {
                _cache.AddOrUpdate(key,
                    new WeakReference<BitmapImage>(value),
                    (k, oldRef) => new WeakReference<BitmapImage>(value));
            }
        }

        public void Remove(string key)
        {
            _cache.TryRemove(key, out _);
        }

        public int Count => _cache.Count;

        public int AliveCount
        {
            get
            {
                return _cache.Values.Count(wr => wr.TryGetTarget(out _));
            }
        }

        private void CleanupExpiredReferences(object state)
        {
            var keysToRemove = new List<string>();

            foreach (var kvp in _cache)
            {
                if (!kvp.Value.TryGetTarget(out _))
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            foreach (var key in keysToRemove)
            {
                _cache.TryRemove(key, out _);
            }

            System.Diagnostics.Debug.WriteLine($"清理缓存：移除 {keysToRemove.Count} 个失效引用，当前缓存数量：{Count}");
        }

        public void Clear()
        {
            _cache.Clear();
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
            _cache.Clear();
        }
    }
}
