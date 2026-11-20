using System;
using System.Collections.Concurrent;
using System.IO;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 登记正在写标签或编码的文件，供目录监听和扫描跳过尚未完成的写入。
    /// </summary>
    public static class AudioFileWriteGate
    {
        /// <summary>登记路径 → 其所在目录（目录的 LastWrite 事件同样由写文件引起，需一并抑制）。</summary>
        private static readonly ConcurrentDictionary<string, string> _activeWrites = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsBeingWritten(string path) => _activeWrites.ContainsKey(path);

        /// <summary>
        /// 该文件系统事件是否由本应用的写入引起（事件路径 = 登记的输出文件本身，
        /// 或其所在目录的 LastWrite 变化）。写入方自己负责入库与刷新，
        /// FileSystemWatcher 对这类事件不应再触发扫描——那只会带来重复刷新
        /// 和与标签重写的文件占用竞争。
        /// </summary>
        public static bool IsOwnWriteEvent(string fullPath)
        {
            if (_activeWrites.IsEmpty) return false;
            if (_activeWrites.ContainsKey(fullPath)) return true;
            foreach (var dir in _activeWrites.Values)
            {
                if (dir == fullPath) return true;
            }
            return false;
        }

        /// <summary>登记写入中的路径，返回释放作用域；using 结束自动解除登记。</summary>
        public static Releaser BeginWrite(string path)
        {
            _activeWrites[path] = Path.GetDirectoryName(path) ?? string.Empty;
            return new Releaser(path);
        }

        public readonly struct Releaser(string path) : IDisposable
        {
            public void Dispose() => _activeWrites.TryRemove(path, out _);
        }
    }
}
