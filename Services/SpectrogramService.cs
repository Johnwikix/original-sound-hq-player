using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Services.AudioAnalysis;
using WinUIMusicPlayer.Services.WebDav;

namespace WinUIMusicPlayer.Services;

/// <summary>按需串行分析；只读本地或完整缓存，按文件版本和 FFT 大小保留最多 16 MiB 的结果。</summary>
public sealed class SpectrogramService(MusicDatabaseService database, RemoteAudioCache remoteCache) : ISpectrogramService
{
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly Dictionary<CacheKey, SpectrogramData> _cache = [];
    private readonly Queue<CacheKey> _order = [];
    private int _bytes;
    private readonly record struct CacheKey(string Path, long Length, long Modified, int FftSize);

    public Task<SpectrogramData?> AnalyzeAsync(SpectrogramInput input, int fftSize, IProgress<double> progress, CancellationToken token)
        => Task.Run(async () =>
        {
            if (!SpectrogramData.ValidFftSize(fftSize)) throw new ArgumentOutOfRangeException(nameof(fftSize));
            await _serial.WaitAsync(token).ConfigureAwait(false);
            try
            {
                token.ThrowIfCancellationRequested();
                RemoteAudioCache.CacheFile? lease = null;
                try
                {
                    string path = input.Path;
                    if (input.SourceId != 0)
                    {
                        var track = await database.GetRemoteTrackAsync(input.MusicId).ConfigureAwait(false);
                        token.ThrowIfCancellationRequested();
                        if (track is null || track.SourceId != input.SourceId) return null;
                        lease = remoteCache.AcquireCompleteRead($"webdav://{track.SourceId}{track.Href}", WebDavLibraryService.ToEntry(track));
                        if (lease?.CompletePath is not { } cached) return null;
                        path = cached;
                    }
                    if (!Path.IsPathFullyQualified(path) || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                        throw new IOException("No complete audio file.");
                    var before = new FileInfo(path);
                    var key = new CacheKey(path, before.Length, before.LastWriteTimeUtc.Ticks, fftSize);
                    if (_cache.TryGetValue(key, out var cachedResult)) return cachedResult;
                    // 共享读取允许现有播放句柄继续工作；文件写入导致版本变化时丢弃结果。
                    using var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
                    var result = await SpectrogramWorkerClient.AnalyzeAsync(path, fftSize, progress, token).ConfigureAwait(false);
                    var after = new FileInfo(path);
                    if (after.Length != key.Length || after.LastWriteTimeUtc.Ticks != key.Modified || lease is { IsCurrent: false })
                        throw new IOException("Audio file changed during analysis.");
                    while (_order.Count > 0 && (_bytes + result.ByteCount > 16 * 1024 * 1024 || _order.Count >= 4))
                    {
                        var expired = _order.Dequeue();
                        _bytes -= _cache[expired].ByteCount;
                        _cache.Remove(expired);
                    }
                    _cache.Add(key, result);
                    _order.Enqueue(key);
                    _bytes += result.ByteCount;
                    return result;
                }
                finally { if (lease is not null) await lease.DisposeAsync().ConfigureAwait(false); }
            }
            finally { _serial.Release(); }
        }, token);
}
