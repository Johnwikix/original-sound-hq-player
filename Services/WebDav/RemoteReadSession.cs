using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace WinUIMusicPlayer.Services.WebDav;

/// <summary>当前曲目的分段读取协调器。最多两个 1 MiB 窗口，前台与补全共用在途窗口。</summary>
public sealed class RemoteReadSession : IAsyncDisposable
{
    private const int WindowSize = 1024 * 1024;
    private readonly WebDavTransport _transport;
    private readonly WebDavConnection _source;
    private readonly RemoteAudioCache _cache;
    private RemoteAudioCache.CacheFile? _file;
    private readonly string _resource;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<long, Window> _windows = [];
    private readonly object _gate = new();
    private TaskCompletionSource _changed = Signal();
    private Task _fill = Task.CompletedTask;
    private bool _playing, _disposed, _rangeSupported = true;
    public WebDavEntry Entry { get; }
    public long NetworkBytes;
    public bool HasCompleteCache => _file?.IsComplete == true;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RemoteReadSession(WebDavTransport transport, WebDavConnection source, WebDavEntry entry, RemoteAudioCache cache, string resource)
    {
        _transport = transport;
        _source = source;
        Entry = entry;
        _cache = cache;
        _resource = resource;
        _file = cache.Acquire(resource, entry);
        cache.Changed += Wake;
        _fill = Task.Run(FillAsync);
    }
    public void SetPlaying(bool playing) { lock (_gate) { _playing = playing; Pulse(); } }
    private void Wake() { lock (_gate) Pulse(); }
    private void Pulse() { var old = _changed; _changed = Signal(); old.TrySetResult(); }

    public async ValueTask<int> ReadAsync(Memory<byte> output, long position, CancellationToken token)
    {
        if (position >= Entry.Length) return 0;
        int requested = (int)Math.Min(output.Length, Entry.Length - position);
        if (_file is not null && _file.Contains(position, requested))
            return await _file.ReadAsync(output[..requested], position, token).ConfigureAwait(false);
        var window = await AcquireAsync(position / WindowSize * WindowSize, token).ConfigureAwait(false);
        try
        {
            int offset = (int)(position - window.Start);
            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (window.Error is not null) throw window.Error;
                    int count = Math.Min(requested, window.Available - offset);
                    if (count > 0) { window.Bytes.AsMemory(offset, count).CopyTo(output); return count; }
                    if (window.Done) return 0;
                    wait = window.Changed.Task;
                }
                await wait.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally { Release(window); }
    }

    private async Task<Window> AcquireAsync(long start, CancellationToken token, bool foreground = true)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            Task wait;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_windows.TryGetValue(start, out var current)) { current.Users++; return current; }
                if (_windows.Count == 2)
                {
                    Window? victim = null;
                    foreach (var candidate in _windows.Values)
                        if (candidate.Done && candidate.Users == 0 && (victim is null || candidate.Start < victim.Start)) victim = candidate;
                    if (victim is not null) { _windows.Remove(victim.Start); ArrayPool<byte>.Shared.Return(victim.Bytes); }
                }
                if (_windows.Count < 2)
                {
                    var window = new Window(start, (int)Math.Min(WindowSize, Entry.Length - start)) { Users = 1 };
                    _windows.Add(start, window);
                    window.Pump = Task.Run(() => PumpAsync(window, foreground));
                    return window;
                }
                wait = _changed.Task;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }
    private void Release(Window window) { lock (_gate) { window.Users--; Pulse(); } }

    private async Task PumpAsync(Window window, bool foreground)
    {
        try
        {
            await using var response = await _transport.OpenAsync(_source, Entry.Href, window.Start, window.Start + window.Count - 1, foreground, _stop.Token).ConfigureAwait(false);
            var range = response.Message.Content.Headers.ContentRange;
            if (response.Message.StatusCode != HttpStatusCode.PartialContent || range?.From != window.Start || range.To != window.Start + window.Count - 1 || range.Length != Entry.Length)
            {
                _rangeSupported = false;
                throw new WebDavException("RangeNotSupported");
            }
            string etag = response.Message.Headers.ETag?.ToString() ?? "";
            if (Entry.ETag.Length != 0 && !Entry.ETag.StartsWith("W/", StringComparison.Ordinal) && etag.Length == 0)
            {
                _file?.Abandon();
                _rangeSupported = false;
                throw new WebDavException("RangeNotSupported");
            }
            if (Entry.ETag.Length != 0 && !Entry.ETag.StartsWith("W/", StringComparison.Ordinal) && etag != Entry.ETag)
                throw new WebDavException("ResourceChanged");
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            int received = 0;
            window.CachedFile = _file;
            while (received < window.Count)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                int count = await response.Stream.ReadAsync(window.Bytes.AsMemory(received, Math.Min(65536, window.Count - received)), idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0) throw new EndOfStreamException();
                Interlocked.Add(ref NetworkBytes, count);
                if (window.CachedFile is { } cached && !cached.TryWrite(window.Start + received, window.Bytes.AsSpan(received, count)))
                    window.CachedFile = null; // 重新开启时补齐整个窗口，不误以为关闭期间的数据已写入。
                received += count;
                lock (_gate)
                {
                    window.Available = received;
                    var old = window.Changed;
                    window.Changed = Signal();
                    old.TrySetResult();
                }
            }
        }
        catch (Exception ex) { lock (_gate) window.Error = ex; }
        finally { lock (_gate) { window.Done = true; window.Changed.TrySetResult(); Pulse(); } }
    }

    private async Task FillAsync()
    {
        try
        {
            long offset = 0;
            while (offset < Entry.Length)
            {
                while (true)
                {
                    Task wait;
                    lock (_gate)
                    {
                        if (!_rangeSupported || _file?.IsComplete == true) return;
                        wait = _changed.Task;
                        if (_playing && _cache.Enabled)
                        {
                            _file ??= _cache.Acquire(_resource, Entry);
                            if (_file?.CanWrite == true) break;
                        }
                    }
                    await wait.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                int count = (int)Math.Min(WindowSize, Entry.Length - offset);
                if (!_file!.Contains(offset, count))
                {
                    var window = await AcquireAsync(offset, _stop.Token, false).ConfigureAwait(false);
                    try
                    {
                        await window.Pump.ConfigureAwait(false);
                        if (window.Error is not null) return;
                        // 开启缓存前已读入的窗口也需落盘。
                        for (int i = 0; i < window.Count; i += 65536)
                        {
                            int bytes = Math.Min(65536, window.Count - i);
                            if (!ReferenceEquals(window.CachedFile, _file) && !_file.Contains(offset + i, bytes) &&
                                !_file.TryWrite(offset + i, window.Bytes.AsSpan(i, bytes))) break;
                        }
                    }
                    finally { Release(window); }
                }
                // 开关在读取/排队期间关闭时保留当前位置，恢复后重试缺口。
                if (!_file.CanWrite) continue;
                offset += count;
                await Task.Delay(10, _stop.Token).ConfigureAwait(false); // 给前台定位与目录请求让出调度机会。
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
    }

    public async Task CopySequentialAsync(Stream destination, CancellationToken token)
    {
        await using var response = await _transport.OpenAsync(_source, Entry.Href, null, null, true, token).ConfigureAwait(false);
        string? etag = response.Message.Headers.ETag?.ToString();
        if (etag is null) _file?.Abandon();
        if (Entry.ETag.Length != 0 && !Entry.ETag.StartsWith("W/", StringComparison.Ordinal) && etag is not null && etag != Entry.ETag)
            throw new WebDavException("ResourceChanged");
        byte[] buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        try
        {
            long position = 0;
            while (true)
            {
                idle.CancelAfter(TimeSpan.FromSeconds(15));
                int count = await response.Stream.ReadAsync(buffer.AsMemory(0, 65536), idle.Token).ConfigureAwait(false);
                idle.CancelAfter(Timeout.InfiniteTimeSpan);
                if (count == 0) break;
                Interlocked.Add(ref NetworkBytes, count);
                if (Entry.Length >= 0 && position + count > Entry.Length) throw new WebDavException("ResourceChanged");
                _file?.TryWrite(position, buffer.AsSpan(0, count));
                position += count;
                await destination.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            }
            if (Entry.Length >= 0 && position != Entry.Length) throw new EndOfStreamException();
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; Pulse(); }
        _cache.Changed -= Wake;
        _stop.Cancel();
        await _fill.ConfigureAwait(false);
        Window[] windows;
        lock (_gate) { windows = new Window[_windows.Count]; _windows.Values.CopyTo(windows, 0); }
        foreach (var window in windows) await window.Pump.ConfigureAwait(false);
        foreach (var window in windows)
        {
            // 调用方先停止桥接请求，再释放 session，避免读者使用已经归还的数组。
            if (window.Users != 0) throw new InvalidOperationException("Remote reads must drain before disposal.");
            ArrayPool<byte>.Shared.Return(window.Bytes);
        }
        _windows.Clear();
        if (_file is not null) await _file.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
    private sealed class Window(long start, int count)
    {
        public readonly long Start = start;
        public readonly int Count = count;
        public readonly byte[] Bytes = ArrayPool<byte>.Shared.Rent(WindowSize);
        public int Users, Available;
        public bool Done;
        public Exception? Error;
        public RemoteAudioCache.CacheFile? CachedFile;
        public TaskCompletionSource Changed = Signal();
        public Task Pump = Task.CompletedTask;
    }
}
