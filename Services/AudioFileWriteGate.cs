using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services
{
    /// <summary>
    /// 应用内音频文件写入互斥协议：任何"写音频文件 → 写标签 → 入库"的流程
    /// （目前是格式转换）通过 <see cref="BeginWrite"/> 登记输出路径，
    /// 期间扫描入库（AutoScan / AddNewMusicAsync）对登记路径直接跳过——
    /// ATL 以 FileShare.Read 打开文件，与容器级标签重写的 ReadWrite 句柄互斥，
    /// 大文件（m4a/wma）标签重写可达数秒，靠重试等写者不可靠。
    /// 写入方在 using 作用域内完成入库后自动释放，扫描方下一轮自然可见。
    /// </summary>
    public static class AudioFileWriteGate
    {
        private static readonly ConcurrentDictionary<string, byte> _activeWrites = new(StringComparer.OrdinalIgnoreCase);

        public static bool IsBeingWritten(string path) => _activeWrites.ContainsKey(path);

        /// <summary>
        /// 是否有任何写入流程在进行中。刷新列表前等待此标志清零，
        /// 避免读到"转换仍在写、产物尚未入库"的中间态。
        /// </summary>
        public static bool AnyActive => !_activeWrites.IsEmpty;

        /// <summary>
        /// 轮询等待所有写入流程结束（超过 <paramref name="timeout"/> 放行兜底，防异常挂起卡死 UI 刷新）。
        /// 供 UI 刷新前调用：写入方完成入库后才释放登记，等待结束后读库必然包含全部转换产物。
        /// </summary>
        public static async Task WaitUntilClearAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        {
            var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
            while (AnyActive)
            {
                if (DateTime.UtcNow >= deadline) return;
                await Task.Delay(100, cancellationToken);
            }
        }

        /// <summary>登记写入中的路径，返回释放作用域；using 结束自动解除登记。</summary>
        public static Releaser BeginWrite(string path)
        {
            _activeWrites[path] = 0;
            return new Releaser(path);
        }

        public readonly struct Releaser(string path) : IDisposable
        {
            public void Dispose() => _activeWrites.TryRemove(path, out _);
        }
    }
}
