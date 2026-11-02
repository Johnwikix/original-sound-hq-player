using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using WinUIMusicPlayer.AudioAnalysis;
using WinUIMusicPlayer.Model.Recommendations;
using WinUIMusicPlayer.Services.WebDav;
using WinUIMusicPlayer.ViewModel;
namespace WinUIMusicPlayer.Services.AudioAnalysis;
/// <summary>拥有有界任务队列、扫描取消与worker；不使用播放进程或网络下载。</summary>
public sealed class AudioAnalysisService(MusicDatabaseService database, RemoteAudioCache cache,
    AppViewModel library, ILogger<AudioAnalysisService> logger, AppLifecycle lifecycle) : WinUIMusicPlayer.Services.Recommendations.IAudioRecommendationSource, IAudioAnalysisSettingsSource
{
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(lifecycle.StoppingToken);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _gate = new();
    private readonly SemaphoreSlim _settings = new(1, 1);
    private readonly SemaphoreSlim _index = new(1, 1);
    private CancellationTokenSource? _scan;
    private Task _loop = Task.CompletedTask;
    private Task? _stopTask;
    private volatile bool _started;
    private volatile bool _enabled = true;
    private AnalysisProgress _progress = new();
    /// <summary>获取最新分析进度。</summary>
    public AnalysisProgress Progress => Volatile.Read(ref _progress);
    /// <summary>通知低频进度，订阅方负责切到UI线程。</summary>
    public event Action? Changed;
    /// <summary>通知推荐信号已提交或功能关闭。</summary>
    public event Action? ResultsChanged;
    /// <summary>显式启动；构造不扫描或创建进程。</summary>
    public async Task StartAsync()
    {
        if (_stop.IsCancellationRequested) return;
        await _settings.WaitAsync(_stop.Token);
        try
        {
            if (_started || _stop.IsCancellationRequested) return;
            await database.WhenInitialized.WaitAsync(_stop.Token);
            _enabled = await database.Analysis.ReadEnabledAsync();
            lock (_gate)
            {
                if (_stop.IsCancellationRequested) return;
                _started = true;
                library.SongsSourceChanged += Wake;
                cache.ContentsChanged += Wake;
                Publish(new(_enabled, Phase: _enabled ? "Idle" : "Disabled"));
                _loop = Task.Run(LoopAsync);
                Wake();
            }
        }
        finally { _settings.Release(); }
    }
    /// <summary>串行持久化开关；关闭取消扫描并由循环等待进程退出。</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        await _settings.WaitAsync();
        try
        {
            if (_stop.IsCancellationRequested) return;
            await database.Analysis.SaveEnabledAsync(enabled);
            _enabled = enabled;
            lock (_gate) if (!enabled) _scan?.Cancel();
            Publish(Progress with { Enabled = enabled, Phase = enabled ? "Idle" : "Disabled" });
            ResultsChanged?.Invoke();
            Wake();
        }
        finally { _settings.Release(); }
    }
    private void Wake()
    {
        if (_stop.IsCancellationRequested) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }
    private void Publish(AnalysisProgress progress)
    {
        Volatile.Write(ref _progress, progress);
        Changed?.Invoke();
    }
    private async Task LoopAsync()
    {
        await using var worker = new AnalysisWorkerClient();
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                if (_enabled) await _wake.WaitAsync(TimeSpan.FromMinutes(5), _stop.Token);
                else await _wake.WaitAsync(_stop.Token);
                if (!_enabled) { await worker.DisposeAsync(); continue; }
                using var scan = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                lock (_gate) { _scan = scan; if (!_enabled) scan.Cancel(); }
                try { await SweepAsync(worker, scan.Token); }
                catch (OperationCanceledException) when (scan.IsCancellationRequested) { await worker.DisposeAsync(); }
                catch (Exception ex)
                {
                    logger.LogWarning("Audio analysis interrupted: {Type}", ex.GetType().Name);
                    Publish(Progress with { Running = false, Phase = "Error", CurrentTitle = "", Pending = 0 });
                    await worker.DisposeAsync();
                }
                finally
                {
                    lock (_gate) _scan = null;
                    if (!_enabled) Publish(Progress with { Enabled = false, Running = false, Pending = 0, Phase = "Disabled", CurrentTitle = "" });
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        finally
        {
            Publish(Progress with { Running = false, Pending = 0, CurrentTitle = "", Phase = _enabled ? "Idle" : "Disabled" });
        }
    }
    private async Task SweepAsync(AnalysisWorkerClient worker, CancellationToken token)
    {
        // 队列只持有最多16条值快照，不提前持有文件或缓存租约。
        var queue = Channel.CreateBounded<AnalysisSource>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        using var batch = CancellationTokenSource.CreateLinkedTokenSource(token);
        int discovered = 0, completed = 0, failed = 0;
        bool changed = false;
        Publish(new(_enabled, Phase: "Scanning"));
        async Task ProduceAsync()
        {
            try
            {
                int after = 0;
                while (true)
                {
                    batch.Token.ThrowIfCancellationRequested();
                    var page = await database.Analysis.ReadSourcesAsync(after).ConfigureAwait(false);
                    if (page.Count == 0) break;
                    foreach (var source in page)
                    {
                        after = source.Id;
                        await queue.Writer.WriteAsync(source, batch.Token).ConfigureAwait(false);
                    }
                }
                queue.Writer.TryComplete();
            }
            catch (Exception ex) { queue.Writer.TryComplete(ex); throw; }
        }
        Task producer = ProduceAsync();
        try
        {
            await foreach (var source in queue.Reader.ReadAllAsync(batch.Token).ConfigureAwait(false))
            {
                await using var lease = source.SourceId == 0 ? null : cache.AcquireCompleteRead($"webdav://{source.SourceId}{source.Href}",
                    new WebDavEntry(source.Href, "", false, source.Length, source.ETag, DateTimeOffset.TryParse(source.Modified, out var date) ? date : null));
                string path = source.SourceId == 0 ? source.Path : lease?.CompletePath ?? "";
                if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\") || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;
                FileInfo info;
                try
                {
                    if (new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network) continue;
                    info = new(path);
                    if (!info.Exists) continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
                discovered++;
                string fingerprint = Fingerprint(source, info);
                if (source.StatusVersion == 2 && source.StatusFingerprint == fingerprint &&
                    (source.StatusDataLength == 1536 || source.StatusRetryTicks > DateTime.UtcNow.Ticks))
                {
                    if (source.StatusDataLength == 1536) completed++; else failed++;
                    if (discovered % 64 == 0) Publish(new(_enabled, false, completed, discovered, queue.Reader.Count, failed, Phase: "Scanning"));
                    continue;
                }
                Publish(new(_enabled, true, completed, discovered, queue.Reader.Count, failed, source.Title, "Running"));
                var record = new AnalysisRecord { MusicId = source.Id, SourceKey = source.SourceKey, Fingerprint = fingerprint };
                try
                {
                    float[] features = await worker.AnalyzeAsync(path, batch.Token).ConfigureAwait(false);
                    batch.Token.ThrowIfCancellationRequested();
                    info.Refresh();
                    // 在途删除、修改、换来源和缓存清理不得提交迟到结果。
                    var current = await database.Analysis.FindSourceAsync(source.Id).ConfigureAwait(false);
                    if (current is null || current.SourceKey != source.SourceKey || !info.Exists ||
                        (source.SourceId != 0 && lease?.IsCurrent != true) || fingerprint != Fingerprint(source, info)) continue;
                    record.Data = MemoryMarshal.AsBytes(features.AsSpan()).ToArray();
                    completed++;
                    changed = true;
                }
                catch (OperationCanceledException) when (batch.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or TimeoutException or OperationCanceledException)
                {
                    logger.LogDebug("Audio analysis skipped track {Id}: {Type}", source.Id, ex.GetType().Name);
                    record.RetryAfterTicks = DateTime.UtcNow.AddHours(24).Ticks;
                    failed++;
                }
                await _index.WaitAsync(batch.Token).ConfigureAwait(false);
                try { await database.Analysis.SaveAsync(record).ConfigureAwait(false); }
                finally { _index.Release(); }
                Publish(new(_enabled, false, completed, discovered, queue.Reader.Count, failed, Phase: "Scanning"));
            }
            await producer.ConfigureAwait(false);
            await database.Analysis.PruneAsync().ConfigureAwait(false);
            Publish(new(_enabled, false, completed, discovered, 0, failed, Phase: "Idle"));
        }
        finally
        {
            batch.Cancel();
            try { await producer.ConfigureAwait(false); } catch (OperationCanceledException) when (batch.IsCancellationRequested) { }
            await worker.DisposeAsync();
            if (changed) ResultsChanged?.Invoke();
        }
    }
    private static string Fingerprint(AnalysisSource source, FileInfo file)
    {
        // 远程完整缓存的mtime由LRU播放触碰更新，不能把它当成音频版本。
        return source.SourceId == 0
            ? string.Concat(source.SourceKey, "\u001f", file.Length, "\u001f", file.LastWriteTimeUtc.Ticks)
            : source.SourceKey;
    }
    /// <summary>流式曲库校准及偏好评分，最多保留一页原始向量。</summary>
    public async Task<Dictionary<int, float>> GetScoresAsync(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history, CancellationToken token)
    {
        if (!_started || !_enabled || _stop.IsCancellationRequested) return [];
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, _stop.Token);
        token = linked.Token;
        await _index.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await database.Analysis.PrepareCacheAsync().ConfigureAwait(false);
            int revision = database.Analysis.CacheRevision;
            string? root = database.Analysis.CacheRootPath;
            var result = await BuildScoresAsync(tracks, history, token).ConfigureAwait(false);
            return revision == database.Analysis.CacheRevision && string.Equals(root, database.Analysis.CacheRootPath, StringComparison.OrdinalIgnoreCase) ? result : [];
        }
        finally { _index.Release(); }
    }
    private async Task<Dictionary<int, float>> BuildScoresAsync(IReadOnlyList<RecommendationTrack> tracks, IReadOnlyList<HomeListening> history, CancellationToken token)
    {
        var scores = new Dictionary<int, float>();
        if (!_enabled) return scores;
        var weights = new Dictionary<int, double>();
        foreach (var row in history) if (row.Weight > 0) weights[row.MusicId] = Math.Log(1 + row.Weight);
        var visible = new HashSet<int>();
        foreach (var track in tracks)
        {
            visible.Add(track.Id);
            if (track.IsFavorite) weights[track.Id] = weights.GetValueOrDefault(track.Id) + 2;
        }
        if (weights.Count == 0) return scores;
        var mean = new double[128]; var squares = new double[128]; var scale = new double[128];
        var clips = new float[384]; var vector = new float[128]; var profile = new float[128];
        int count = 0;
        int pass = 0;
        VisitAnalysisVector visitor = Process;
        for (; pass < 3; pass++)
        {
            int after = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (!_enabled) return [];
                int next = await database.Analysis.VisitVectorsAsync(after, visitor, token).ConfigureAwait(false);
                if (next == after) break;
                after = next;
            }
            if (pass == 0)
            {
                if (count < 16) return scores;
                for (int i = 0; i < 128; i++) scale[i] = Math.Sqrt(Math.Max(0, squares[i] / count));
            }
            if (pass == 1)
            {
                if (FeatureMath.Dot(profile, profile) <= 1e-12f) return scores;
                FeatureMath.Normalize(profile);
            }
        }
        return scores;
        void Process(int musicId, ReadOnlySpan<float> raw)
        {
            if (!visible.Contains(musicId)) return;
            foreach (float value in raw) if (!float.IsFinite(value)) return;
            if (pass == 0)
            {
                count++;
                for (int i = 0; i < 128; i++)
                {
                    double value = ((double)raw[i] + raw[128 + i] + raw[256 + i]) / 3;
                    double delta = value - mean[i];
                    mean[i] += delta / count;
                    squares[i] += delta * (value - mean[i]);
                }
                return;
            }
            if (pass == 1 && !weights.ContainsKey(musicId)) return;
            FeatureMath.Calibrate(raw, mean, scale, clips, vector);
            if (pass == 1)
            {
                double weight = weights[musicId];
                for (int i = 0; i < 128; i++) profile[i] += (float)(vector[i] * weight);
            }
            else scores[musicId] = Math.Clamp(FeatureMath.Dot(vector, profile), 0, 1);
        }
    }
    /// <summary>停止入口、取消、等待扫描及进程释放后才允许数据库收尾。</summary>
    public Task StopAsync()
    {
        lock (_gate)
        {
            if (_stopTask is not null) return _stopTask;
            library.SongsSourceChanged -= Wake;
            cache.ContentsChanged -= Wake;
            _stop.Cancel();
            return _stopTask = FinishAsync();
        }
    }
    private async Task FinishAsync()
    {
        await _loop.ConfigureAwait(false);
        await _settings.WaitAsync().ConfigureAwait(false);
        _settings.Release();
        await _index.WaitAsync().ConfigureAwait(false);
        _index.Release();
        Changed = null;
        ResultsChanged = null;
        _stop.Dispose();
        _wake.Dispose();
        _settings.Dispose();
        _index.Dispose();
    }
}
