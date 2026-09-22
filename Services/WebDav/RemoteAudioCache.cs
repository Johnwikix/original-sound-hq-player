using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>只管理应用专用目录；完整文件原子提交，活动文件由租约保护。</summary>
public sealed class RemoteAudioCache
{
    private readonly object _gate = new();
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _reserved = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deleteOnRelease = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _initialized = new(StringComparer.OrdinalIgnoreCase);
    private int _generation;
    private string _directory = "";
    private long _limit = 10L * 1024 * 1024 * 1024;
    private volatile bool _enabled;
    public bool Enabled => _enabled;
    public event Action? Changed;
    public void Configure(bool enabled, string parent, long limit)
    {
        lock (_gate)
        {
            string directory = WebDavCachePaths.Audio(parent);
            if (!string.Equals(directory, _directory, StringComparison.OrdinalIgnoreCase)) _generation++;
            _enabled = enabled;
            _directory = directory;
            _limit = Math.Max(0, limit);
        }
        Changed?.Invoke();
    }
    public CacheFile? Acquire(string resource, WebDavEntry entry)
    {
        lock (_gate)
        {
            if (_directory.Length == 0 || entry.Length <= 0) return null;
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resource + "\n" + entry.ETag + "\n" + entry.Modified + "\n" + entry.Length)));
            string complete = Path.Combine(_directory, key + ".audio");
            if (_active.Contains(complete)) return null;
            try
            {
                Directory.CreateDirectory(_directory);
                if (_initialized.Add(_directory))
                    foreach (string partial in Directory.EnumerateFiles(_directory, "*.audio.*.part"))
                        try { File.Delete(partial); } catch (IOException) { }
                if (File.Exists(complete) && new FileInfo(complete).Length == entry.Length)
                {
                    File.SetLastWriteTimeUtc(complete, DateTime.UtcNow);
                    var lease = new CacheFile(this, complete, entry.Length, true, _generation);
                    _active.Add(complete);
                    return lease;
                }
                // 无强版本不跨 Range 响应拼接。普通顺序播放仍能使用网络。
                if (!Enabled || entry.ETag.Length == 0 || entry.ETag.StartsWith("W/", StringComparison.Ordinal) || entry.Length > _limit) return null;
                long used = 0;
                var files = new List<FileInfo>();
                foreach (var path in Directory.EnumerateFiles(_directory, "*.audio"))
                {
                    var file = new FileInfo(path);
                    used += file.Length;
                    if (!_active.Contains(path)) files.Add(file);
                }
                foreach (var reservation in _reserved)
                    if (IsInCurrentDirectory(reservation.Key)) used += reservation.Value;
                files.Sort(static (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
                foreach (var file in files)
                {
                    if (used + entry.Length <= _limit) break;
                    long length = file.Length;
                    file.Delete();
                    used -= length;
                }
                if (used + entry.Length > _limit || new DriveInfo(Path.GetPathRoot(_directory)!).AvailableFreeSpace < entry.Length + 2L * 1024 * 1024 * 1024) return null;
                _active.Add(complete);
                _reserved[complete] = entry.Length;
                try { return new(this, complete, entry.Length, false, _generation); }
                catch { _active.Remove(complete); _reserved.Remove(complete); throw; }
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
        }
    }
    internal void Release(string file)
    {
        lock (_gate)
        {
            _active.Remove(file);
            _reserved.Remove(file);
            if (_deleteOnRelease.Remove(file))
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private bool CanWrite(string file, int generation, long length)
    {
        lock (_gate) return Enabled && CanCommit(file, generation, length);
    }
    private bool CanCommit(string file, int generation, long length)
    {
        // 关闭自动下载不丢弃已经完整写入的数据；清理、换目录仍使旧租约失效。
        lock (_gate) return generation == _generation && length <= _limit && !_deleteOnRelease.Contains(file);
    }

    private bool IsInCurrentDirectory(string file)
        => string.Equals(Path.GetDirectoryName(file), _directory, StringComparison.OrdinalIgnoreCase);

    private bool Commit(string partial, string complete, int generation, long length)
    {
        lock (_gate)
        {
            // 关闭文件与提交之间仍可能切换目录/清理，必须在同一锁内复查并发布。
            if (!CanCommit(complete, generation, length)) return false;
            File.Move(partial, complete, true);
            return true;
        }
    }
    public long GetSize()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return 0;
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.audio")) total += new FileInfo(file).Length;
            foreach (var file in Directory.EnumerateFiles(_directory, "*.audio.*.part")) total += new FileInfo(file).Length;
            return total;
        }
    }
    public void Clear()
    {
        lock (_gate)
        {
            if (!Directory.Exists(_directory)) return;
            _generation++;
            foreach (string path in _active)
                if (IsInCurrentDirectory(path)) _deleteOnRelease.Add(path);
            foreach (string path in Directory.EnumerateFiles(_directory, "*.audio"))
                if (!_active.Contains(path)) File.Delete(path);
        }
        Changed?.Invoke();
    }

    /// <summary>写盘队列最多 2 MiB，慢盘降级；网络缓冲与写盘缓冲的所有权分开。</summary>
    public sealed class CacheFile : IAsyncDisposable
    {
        private readonly RemoteAudioCache _owner;
        private readonly string _completePath, _partialPath;
        private readonly long _length;
        private readonly FileStream _file;
        private readonly Channel<(long Offset, byte[] Bytes, int Count)>? _writes;
        private readonly Task _writer;
        private readonly List<(long Start, long End)> _ranges = [];
        private readonly object _gate = new();
        private volatile bool _failed, _disposed;
        private readonly int _generation;
        public bool IsComplete { get; private set; }
        public bool CanWrite => !IsComplete && !_failed && !_disposed && _owner.CanWrite(_completePath, _generation, _length);
        public string? CompletePath => IsComplete ? _completePath : null;
        public void Abandon() => _failed = true;

        internal CacheFile(RemoteAudioCache owner, string path, long length, bool complete, int generation)
        {
            _generation = generation;
            _owner = owner;
            _completePath = path;
            _partialPath = path + "." + Guid.NewGuid().ToString("N") + ".part";
            _length = length;
            IsComplete = complete;
            _file = new FileStream(complete ? path : _partialPath, complete ? FileMode.Open : FileMode.CreateNew,
                complete ? FileAccess.Read : FileAccess.ReadWrite, FileShare.Read, 1, FileOptions.Asynchronous | FileOptions.RandomAccess);
            if (complete) { _ranges.Add((0, length)); _writer = Task.CompletedTask; }
            else
            {
                _writes = Channel.CreateBounded<(long, byte[], int)>(new BoundedChannelOptions(32) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
                _writer = WriteLoopAsync();
            }
        }
        public bool Contains(long start, int count)
        {
            lock (_gate)
            {
                foreach (var range in _ranges) if (range.Start <= start && range.End >= start + count) return true;
                return false;
            }
        }
        public async ValueTask<int> ReadAsync(Memory<byte> output, long offset, CancellationToken token)
        {
            if (!Contains(offset, output.Length)) return 0;
            return await RandomAccess.ReadAsync(_file.SafeFileHandle, output, offset, token).ConfigureAwait(false);
        }
        public bool TryWrite(long offset, ReadOnlySpan<byte> data)
        {
            if (!CanWrite || data.Length > 65536 || _writes is null) return false;
            byte[] owned = ArrayPool<byte>.Shared.Rent(65536);
            data.CopyTo(owned);
            if (!_writes.Writer.TryWrite((offset, owned, data.Length)))
            {
                ArrayPool<byte>.Shared.Return(owned);
                _failed = true;
                return false;
            }
            return true;
        }
        private async Task WriteLoopAsync()
        {
            await foreach (var item in _writes!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                try
                {
                    if (_failed) continue;
                    await RandomAccess.WriteAsync(_file.SafeFileHandle, item.Bytes.AsMemory(0, item.Count), item.Offset).ConfigureAwait(false);
                    lock (_gate)
                    {
                        long start = item.Offset, end = item.Offset + item.Count;
                        for (int i = _ranges.Count - 1; i >= 0; i--)
                        {
                            var range = _ranges[i];
                            if (range.End < start || range.Start > end) continue;
                            start = Math.Min(start, range.Start);
                            end = Math.Max(end, range.End);
                            _ranges.RemoveAt(i);
                        }
                        _ranges.Add((start, end));
                    }
                }
                catch (IOException) { _failed = true; }
                catch (UnauthorizedAccessException) { _failed = true; }
                finally { ArrayPool<byte>.Shared.Return(item.Bytes); }
            }
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            _writes?.Writer.TryComplete();
            try
            {
                await _writer.ConfigureAwait(false);
                bool full = !IsComplete && !_failed && _owner.CanCommit(_completePath, _generation, _length) && Contains(0, checked((int)Math.Min(int.MaxValue, _length)));
                lock (_gate) full = full && _ranges.Count == 1 && _ranges[0] == (0, _length);
                if (full) _file.Flush(true);
                await _file.DisposeAsync().ConfigureAwait(false);
                if (full) IsComplete = _owner.Commit(_partialPath, _completePath, _generation, _length);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                await _file.DisposeAsync().ConfigureAwait(false);
                try { if (File.Exists(_partialPath)) File.Delete(_partialPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { _owner.Release(_completePath); }
            }
        }
    }
}
